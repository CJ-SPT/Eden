using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat.Npcs;
using Eden.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private readonly NpcProfileCatalog _simulationProfiles = new();
    private readonly ResidentMap<int, long[]> _systemNpcs = [];
    private readonly ResidentMap<int, long> _populationDue = [];
    private int? _populationRefreshSystem;

    private void ObserveAsteroidPopulation(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_populationDue.Count == 0)
        {
            return;
        }
        if (_populationRefreshSystem is { } scoped)
        {
            _populationDue.Remove(scoped);
            return;
        }
        var ids = changes.Where(change => change.Table == "WorldAsteroid").Select(change => change.Id).ToArray();
        var systems =
            owner.Economy.IsLoaded || changes.Any(c => c.Resident)
                ? ids.Select(id => owner.Economy.Peek<WorldAsteroid>(id)?.SystemId).OfType<int>().ToArray()
                : Loading(() =>
                {
                    using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
                    return world.Set<WorldAsteroid>().Where(a => EF.Parameter(ids).Contains(a.Id)).Select(a => a.SystemId).ToArray();
                });
        if (systems.Length != ids.Length)
        {
            // Deleted identities have no remaining location; retain conservative invalidation.
            _populationDue.Clear();
        }
        else
        {
            foreach (int system in systems.Distinct())
            {
                _populationDue.Remove(system);
            }
        }
    }

    private void InvalidatePopulation(int system)
    {
        _populationDue.Remove(system);
        _systemNpcs.Remove(system);
        _contacts.Remove(system);
        _npcSceneIndices.Remove(system);
        foreach (var key in _guardGroups.Keys.Where(k => k.System == system).ToArray())
        {
            _guardGroups.Remove(key);
        }
    }

    private void ObserveNpcPopulation(long id, bool resident = false)
    {
        if (_populationRefreshSystem is { } scoped)
        {
            InvalidatePopulation(scoped);
            return;
        }
        if (_npcs.TryGetValue(id, out var previous))
        {
            InvalidatePopulation(previous.SystemId);
        }
        int? current =
            resident || owner.Economy.IsLoaded
                ? owner.Economy.Peek<WorldNpc>(id)?.SystemId
                : Loading(() =>
                {
                    using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
                    return world.Set<WorldNpc>().Where(n => n.Id == id).Select(n => (int?)n.SystemId).SingleOrDefault();
                });
        if (current is int system)
        {
            InvalidatePopulation(system);
        }
    }

    private void ObserveEncounterPopulation(long id, bool resident = false)
    {
        if (_populationRefreshSystem is { } scoped)
        {
            InvalidatePopulation(scoped);
            return;
        }
        int? current =
            resident || owner.Economy.IsLoaded
                ? owner.Economy.Peek<WorldNpcEncounter>(id)?.SystemId
                : Loading(() =>
                {
                    using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
                    return world.Set<WorldNpcEncounter>().Where(e => e.Id == id).Select(e => (int?)e.SystemId).SingleOrDefault();
                });
        if (current is int system)
        {
            InvalidatePopulation(system);
        }
    }

    private readonly ResidentMap<long, IReadOnlyDictionary<DogmaAttributeId, double>> _effectiveNpcs = [];

    private sealed record LivePosition(int? System, SpacePosition Position);

    private readonly ResidentMap<long, LivePosition> _positions = [];
    private readonly ResidentMap<long, LivePosition?> _savedPositions = [];
    private readonly ResidentSet<long> _dirtyPositions = [];

    internal SpacePosition? InformationPosition(long shipId) => _positions.GetValueOrDefault(shipId)?.Position;

    internal SpacePosition? DirectionalPosition(long id) =>
        InformationPosition(id)
        ?? _liveDrones.GetValueOrDefault(id)?.Contact.Position
        ?? (_staleNpcs.Contains(id) ? null : _npcs.GetValueOrDefault(id)?.Position);

    private LiveCombatNpc[] SystemNpcs(int system, long now)
    {
        if (!_systemNpcs.TryGetValue(system, out var ids))
        {
            _ = DroneControllers();
            ids = Loading(() =>
            {
                using WorldDbContext world = new(database.Path) { Participants = new(systems: [system]) };
                return world
                    .Set<WorldNpc>()
                    .Where(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null)
                    .OrderBy(n => n.Id)
                    .Select(n => n.Id)
                    .ToArray();
            });
            _systemNpcs[system] = ids;
        }
        LoadNpcs(ids.Where(id => !_npcs.ContainsKey(id) || _staleNpcs.Contains(id)).ToArray(), now);
        return ids.Select(id => Npc(id, now)).OfType<LiveCombatNpc>().Where(n => n.Available && n.SystemId == system).ToArray();
    }

    private IReadOnlyDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> Effective(LiveCombatNpc[] npcs)
    {
        var missing = npcs.Where(n => !_effectiveNpcs.ContainsKey(n.Id)).ToArray();
        if (missing.Length != 0)
        {
            Loading(() =>
            {
                using WorldDbContext world = new(database.Path)
                {
                    Participants = new(resources: missing.Select(npc => new SimulationResource(SharedSimulationResource.Npc, npc.Id))),
                };
                var effects = PilotTackleStore.NpcTackles(world, missing.Select(n => n.Id).ToArray());
                foreach (var npc in missing)
                {
                    _effectiveNpcs[npc.Id] = TackleDefinitions.NpcAttributes(
                        npc.Attributes,
                        effects[npc.Id].Select(t => t.Module()).ToArray()
                    );
                }
                return true;
            });
        }
        // Consumers index the current NPC IDs synchronously under the owner.
        // Reuse the resident index instead of cloning it for each observer.
        return _effectiveNpcs;
    }

    internal IReadOnlyList<ShipMotionResult>? Advance(
        int system,
        int stamp,
        long now,
        IReadOnlyList<ShipMotion> ships,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        return Execute(() => AdvanceOwned(system, stamp, now, ships, objects), nameof(Advance));
    }

    private IReadOnlyList<ShipMotionResult>? AdvanceOwned(
        int system,
        int stamp,
        long now,
        IReadOnlyList<ShipMotion> ships,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        using var ownership = owner.Enter();
        if (!simulationObjects || !CanRun)
        {
            return null;
        }
        Trim();
        var npcs = SystemNpcs(system, now);
        var result = LiveSystemMotionRules.Advance(stamp, now, ships, objects, npcs, Aggression(), Effective(npcs));
        // Load saved position identity before publishing the staged simulation.
        var missing = ships.Where(s => !_positions.ContainsKey(s.Id)).Select(s => s.Id).ToArray();
        if (missing.Length != 0)
        {
            Loading(() =>
            {
                if (owner.Economy.IsLoaded)
                {
                    foreach (long id in missing)
                    {
                        var row = owner.Economy.Peek<Eden.Data.World.Docking.FlightPosition>(id);
                        _savedPositions[id] = row is null ? null : new(row.SystemId, new(row.X, row.Y, row.Z));
                    }
                    return true;
                }
                using WorldDbContext world = new(database.Path);
                var rows = world.Positions.AsNoTracking().Where(p => missing.Contains(p.ShipId)).ToDictionary(p => p.ShipId);
                foreach (long id in missing)
                {
                    _savedPositions[id] = rows.TryGetValue(id, out var row) ? new(row.SystemId, new(row.X, row.Y, row.Z)) : null;
                }
                return true;
            });
        }
        var warping = result
            .Ships.Where(r => r.Ship.Navigation.Warp is not null && ships.Single(s => s.Id == r.Ship.Id).Navigation.Warp is null)
            .ToArray();
        if (warping.Length != 0)
        {
            // Warp task progress retains its transaction, and is paid only on entry.
            Flush();
            using WorldDbContext world = new(database.Path);
            using CatalogDbContext catalog = new(database.CatalogPath);
            using var transaction = world.Database.BeginTransaction();
            foreach (var ship in warping)
            {
                var pilot = world.Pilots.SingleOrDefault(p => p.ActiveShipId == ship.Ship.Id);
                if (pilot is not null)
                {
                    PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.Warped);
                }
            }
            world.SaveChanges();
            transaction.Commit();
        }
        if (
            result.Npcs.Any(n => n.Position != _npcs[n.Id].Position || n.Navigation != _npcs[n.Id].Navigation)
            || result.Ships.Any(s => !_positions.TryGetValue(s.Ship.Id, out var prior) || prior.Position != s.Ship.Position)
        )
        {
            _acquisitions.Clear();
        }
        foreach (var npc in result.Npcs)
        {
            PublishNpc(npc);
        }
        foreach (var player in result.Ships)
        {
            long id = player.Ship.Id;
            PublishPilotPosition(id, system, player.Ship.Position);
        }
        database.MotionStamps[system] = stamp;
        foreach (var collision in result.Collisions.Where(c => c.Bumped))
        {
            database.CollisionStamps[collision.Id] = stamp;
        }
        return result.Ships;
    }

    // Both connected and autonomous pilots publish motion under the resident owner after undocking.
    internal void PublishPilotPosition(long id, int system, SpacePosition position)
    {
        Execute(() => PublishPilotPositionOwned(id, system, position), nameof(PublishPilotPosition));
    }

    private void PublishPilotPositionOwned(long id, int system, SpacePosition position)
    {
        using var ownership = owner.Enter();
        owner.TrackShip(id, system);
        if (_positions.TryGetValue(id, out var previous) && previous.Position != position)
        {
            _acquisitions.Clear();
        }
        _savedPositions.TryAdd(id, null);
        _positions[id] = new(_savedPositions[id] is { } saved ? saved.System : system, position);
        if (_positions[id] != _savedPositions[id])
        {
            _dirtyPositions.Add(id);
            owner.ObjectsChanged();
        }
        if (_shipPilots.TryGetValue(id, out long pilot) && _ships.TryGetValue(pilot, out var ship))
        {
            _ships[pilot] = ship with { Position = position, SystemId = system };
        }
    }

    private LiveCombatNpc PublishNpc(LiveCombatNpc npc)
    {
        var previous = _npcs[npc.Id];
        if (previous == npc)
        {
            return previous;
        }
        if (previous.Position != npc.Position || previous.Navigation != npc.Navigation)
        {
            _acquisitions.Clear();
        }
        SetNpc(npc);
        _dirtyNpcs.Add(npc.Id);
        owner.ObjectsChanged();
        return npc;
    }

    private LiveCombatShip Positioned(LiveCombatShip ship) =>
        _positions.TryGetValue(ship.ShipId, out var position)
            ? ship with
            {
                Position = position.Position,
                SystemId = position.System ?? ship.SystemId,
            }
            : ship;

    private LiveCombatNpc[] AdvanceSimulationNpcs(int system, long now, Action refresh, Func<long> nextPopulation)
    {
        if (!_populationDue.TryGetValue(system, out long due) || now >= due)
        {
            Flush();
            int? previousScope = _populationRefreshSystem;
            _populationRefreshSystem = system;
            try
            {
                refresh();
            }
            finally
            {
                _populationRefreshSystem = previousScope;
            }
            _systemNpcs.Remove(system);
            _populationDue[system] = nextPopulation();
        }
        var npcs = SystemNpcs(system, now);
        var effective = database.MotionStamps.ContainsKey(system) ? null : Effective(npcs);
        var aggression = Aggression();
        for (int index = 0; index < npcs.Length; index++)
        {
            var npc = npcs[index];
            var active = aggression.GetValueOrDefault(npc.Id);
            if (effective is not null)
            {
                npc = LiveSystemMotionRules.Move(
                    npc,
                    effective[npc.Id],
                    active is null ? null : new(active.ShipId, 0, 0, 0, 0, npc.Position, 0),
                    active?.ReadyAt ?? long.MaxValue,
                    now,
                    0,
                    contactsOnly: true
                );
            }
            else if (active is null && npc.Navigation is NpcNavigationState.Pursuing or NpcNavigationState.Orbiting)
            {
                npc = npc with { Navigation = NpcNavigationState.Returning };
            }
            else
            {
                continue;
            }
            npcs[index] = PublishNpc(npc);
        }
        return npcs;
    }

    internal IReadOnlyList<NpcSimulationContact>? Simulate(int system, long now, Action refresh, Func<long> nextPopulation)
    {
        return Execute(() => SimulateOwned(system, now, refresh, nextPopulation), nameof(Simulate));
    }

    private IReadOnlyList<NpcSimulationContact>? SimulateOwned(int system, long now, Action refresh, Func<long> nextPopulation)
    {
        using var ownership = owner.Enter();
        if (!simulationObjects || !CanRun)
        {
            return null;
        }
        Trim();
        return AdvanceSimulationNpcs(system, now, refresh, nextPopulation)
            .Select(n => new NpcSimulationContact(n.Id, n.Position, n.Transit is not null))
            .ToArray();
    }

    internal void ReleaseObserverContacts(int system)
    {
        Execute(() => ReleaseObserverContactsOwned(system), nameof(ReleaseObserverContacts));
    }

    private void ReleaseObserverContactsOwned(int system)
    {
        using var ownership = owner.Enter();
        _contacts.Remove(system);
        _npcSceneIndices.Remove(system);
        foreach (long id in _sharedNpcContacts.Where(p => p.Value.Source.SystemId == system).Select(p => p.Key).ToArray())
        {
            _sharedNpcContacts.Remove(id);
        }
        database.MotionStamps.TryRemove(system, out _);
    }

    private IReadOnlyList<NpcContact> SimulationContacts(
        int system,
        long now,
        Action refresh,
        Func<long> nextPopulation,
        SpacePosition? position = null
    )
    {
        var npcs = AdvanceSimulationNpcs(system, now, refresh, nextPopulation);
        if (position is not null)
        {
            var visible = SceneIndex(system, npcs).Find(position);
            if (visible.Count != npcs.Length)
            {
                npcs = visible.Select(id => _npcs[id]).ToArray();
            }
        }
        var effective = Effective(npcs);
        var aggression = Aggression();
        List<NpcContact> contacts = new(npcs.Length);
        bool motionStamped = database.MotionStamps.TryGetValue(system, out int stamp);
        foreach (var npc in npcs)
        {
            var active = aggression.GetValueOrDefault(npc.Id);
            bool bumped = database.CollisionStamps.GetValueOrDefault(npc.Id, -1) == stamp;
            if (motionStamped && TrySharedNpcContact(npc, active, stamp, bumped, now, out var shared))
            {
                contacts.Add(shared);
                continue;
            }
            double duration = npc.Attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime);
            NpcWeaponContact? weapon =
                active?.LastShotAt is long started && now >= started && now - started < duration * TimeSpan.TicksPerMillisecond
                    ? new(
                        active.LastTargetId ?? active.ShipId,
                        checked((int)npc.Attributes.GetValueOrDefault(DogmaAttributeId.EntityTurretType)),
                        started,
                        duration
                    )
                    : null;
            NpcContact contact = new(
                npc.Id,
                npc.TypeId,
                npc.GroupId,
                npc.CategoryId,
                npc.CorporationId,
                npc.Name,
                system,
                npc.Position,
                npc.Mass,
                (float)npc.Radius,
                (float)effective[npc.Id].GetValueOrDefault(DogmaAttributeId.MaxVelocity),
                NpcMotionRules.Agility(npc.Attributes),
                new(npc.Id, npc.Health.Defense, npc.Health.At(now)),
                weapon,
                LiveSystemMotionRules.Contact(npc, effective[npc.Id], active?.ShipId ?? 0, now),
                stamp,
                bumped,
                npc.Role,
                npc.FactionId,
                npc.RepairState.Revision,
                EwarContacts(npc.Id),
                npc.Activity,
                npc.Transit?.Course(
                    stamp,
                    now,
                    new(
                        0,
                        (float)npc.Radius,
                        npc.Mass,
                        NpcMotionRules.Agility(npc.Attributes),
                        (float)effective[npc.Id][DogmaAttributeId.MaxVelocity],
                        0
                    )
                )
            )
            {
                AnchorKind = npc.AnchorKind,
                AnchorId = npc.AnchorId,
                StandingThresholds = npc.StandingThresholds,
            };
            contacts.Add(contact);
            if (motionStamped)
            {
                ShareNpcContact(npc, active, stamp, bumped, now, contact);
            }
        }
        return contacts;
    }

    private void SimulationAcquire(IReadOnlyList<NpcTargetCandidate> candidates, long now)
    {
        var ships = AcquisitionShips(candidates).ToArray();
        if (ships.Length == 0)
        {
            return;
        }
        var aggression = Aggression();
        var npcs = ships.Select(s => s.SystemId).Distinct().SelectMany(s => SystemNpcs(s, now)).ToArray();
        var additions = LiveNpcAcquisitionRules.Acquire(
            npcs,
            ships.Select(s => new LiveNpcTarget(s.ShipId, s.SystemId, s.Position, s.Radius)).ToArray(),
            aggression.Keys.ToHashSet(),
            now
        );
        foreach (var addition in additions)
        {
            aggression[addition.NpcId] = addition;
            _dirtyAggressions.Add(addition.NpcId);
            owner.ObjectsChanged();
        }
    }

    private sealed record AcquisitionShip(long PilotId, long ShipId, int SystemId, SpacePosition Position, double Radius, int GroupId);

    private readonly ResidentMap<long, AcquisitionShip?> _acquisitionShips = [];

    private IEnumerable<AcquisitionShip> AcquisitionShips(IReadOnlyList<NpcTargetCandidate> candidates)
    {
        var missing = candidates.Select(c => c.PilotId).Distinct().Where(id => !_acquisitionShips.ContainsKey(id)).ToArray();
        if (missing.Length != 0)
        {
            Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                var loaded = missing.ToDictionary(id => id, _ => (AcquisitionShip?)null);
                var pilots = world
                    .Pilots.Include(p => p.ActiveShip)
                        .ThenInclude(s => s.Position)
                    .Where(p => missing.Contains(p.Id) && p.ActiveShip.Position != null)
                    .ToArray();
                foreach (var pilot in pilots)
                {
                    var ship = pilot.ActiveShip;
                    var position = ship.Position!;
                    var type = catalog.RequireItem(ship.TypeId);
                    int system = position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
                    loaded[pilot.Id] = new(
                        pilot.Id,
                        ship.Id,
                        system,
                        new(position.X, position.Y, position.Z),
                        type.Radius ?? 0,
                        type.GroupId
                    );
                }
                foreach (var pair in loaded)
                {
                    _acquisitionShips[pair.Key] = pair.Value;
                }
                return true;
            });
        }
        return candidates
            .Select(c => _acquisitionShips[c.PilotId])
            .OfType<AcquisitionShip>()
            .Where(s => candidates.Contains(new(s.PilotId, s.ShipId)) && s.GroupId != FleetDefinitions.CapsuleGroup)
            .Select(s =>
                _positions.TryGetValue(s.ShipId, out var current)
                    ? s with
                    {
                        Position = current.Position,
                        SystemId = current.System ?? s.SystemId,
                    }
                    : s
            );
    }

    private void ClearSimulation(bool retainPositions)
    {
        _acquisitionShips.Clear();
        _systemNpcs.Clear();
        _populationDue.Clear();
        _effectiveNpcs.Clear();
        if (!retainPositions)
        {
            _positions.Clear();
            _savedPositions.Clear();
            _dirtyPositions.Clear();
        }
        _sharedNpcContacts.Clear();
    }
}
