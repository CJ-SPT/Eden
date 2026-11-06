using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Propulsion;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal sealed partial class LiveCombatRuntime
{
    private NpcProfileCatalog _simulationProfiles = new();
    private readonly Dictionary<int, long[]> _systemNpcs = [];
    private readonly Dictionary<int, long> _populationDue = [];
    private readonly Dictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> _effectiveNpcs = [];

    private sealed record LivePosition(int? System, SpacePosition Position);

    private readonly Dictionary<long, LivePosition> _positions = [];
    private readonly Dictionary<long, LivePosition?> _savedPositions = [];
    private readonly HashSet<long> _dirtyPositions = [];

    private LiveCombatNpc[] SystemNpcs(int system, long now)
    {
        if (!_systemNpcs.TryGetValue(system, out var ids))
        {
            ids = Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                return world
                    .Set<WorldNpc>()
                    .Where(n => n.SystemId == system && n.DestroyedAt == null && n.RetiredAt == null)
                    .OrderBy(n => n.Id)
                    .Select(n => n.Id)
                    .ToArray();
            });
            _systemNpcs[system] = ids;
        }
        return ids.Select(id => Npc(id, now)).OfType<LiveCombatNpc>().Where(n => n.Available && n.SystemId == system).ToArray();
    }

    private IReadOnlyDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> Effective(LiveCombatNpc[] npcs)
    {
        var missing = npcs.Where(n => !_effectiveNpcs.ContainsKey(n.Id)).ToArray();
        if (missing.Length != 0)
        {
            Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
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
        return npcs.ToDictionary(n => n.Id, n => _effectiveNpcs[n.Id]);
    }

    internal IReadOnlyList<ShipMotionResult>? Advance(
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
            _positions[id] = new(_savedPositions[id] is { } saved ? saved.System : system, player.Ship.Position);
            if (_positions[id] != _savedPositions[id])
            {
                _dirtyPositions.Add(id);
                owner.ObjectsChanged();
            }
            if (_shipPilots.TryGetValue(id, out long pilot) && _ships.TryGetValue(pilot, out var ship))
            {
                _ships[pilot] = ship with { Position = player.Ship.Position, SystemId = system };
            }
        }
        database.MotionStamps[system] = stamp;
        foreach (var collision in result.Collisions.Where(c => c.Bumped))
        {
            database.CollisionStamps[collision.Id] = stamp;
        }
        return result.Ships;
    }

    private void PublishNpc(LiveCombatNpc npc)
    {
        if (_npcs[npc.Id] != npc)
        {
            if (_npcs[npc.Id].Position != npc.Position || _npcs[npc.Id].Navigation != npc.Navigation)
            {
                _acquisitions.Clear();
            }
            _npcs[npc.Id] = npc;
            _dirtyNpcs.Add(npc.Id);
            owner.ObjectsChanged();
        }
    }

    private LiveCombatShip Positioned(LiveCombatShip ship) =>
        _positions.TryGetValue(ship.ShipId, out var position)
            ? ship with
            {
                Position = position.Position,
                SystemId = position.System ?? ship.SystemId,
            }
            : ship;

    private IReadOnlyList<NpcContact> SimulationContacts(int system, long now, Action refresh, Func<long> nextPopulation)
    {
        if (!_populationDue.TryGetValue(system, out long due) || now >= due)
        {
            Flush();
            refresh();
            _systemNpcs.Remove(system);
            _populationDue[system] = nextPopulation();
        }
        var npcs = SystemNpcs(system, now);
        var effective = Effective(npcs);
        var aggression = Aggression();
        List<NpcContact> contacts = [];
        List<LiveCombatNpc> updated = [];
        foreach (var source in npcs)
        {
            var active = aggression.GetValueOrDefault(source.Id);
            var npc = source;
            if (!database.MotionStamps.ContainsKey(system))
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
            updated.Add(npc);
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
            int stamp = database.MotionStamps.GetValueOrDefault(system);
            contacts.Add(
                new(
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
                    database.CollisionStamps.GetValueOrDefault(npc.Id, -1) == stamp,
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
            );
        }
        foreach (var npc in updated)
        {
            PublishNpc(npc);
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

    private readonly Dictionary<long, AcquisitionShip?> _acquisitionShips = [];

    private IEnumerable<AcquisitionShip> AcquisitionShips(IReadOnlyList<NpcTargetCandidate> candidates)
    {
        var missing = candidates.Select(c => c.PilotId).Distinct().Where(id => !_acquisitionShips.ContainsKey(id)).ToArray();
        if (missing.Length != 0)
        {
            Loading(() =>
            {
                using WorldDbContext world = new(database.Path);
                using CatalogDbContext catalog = new(database.CatalogPath);
                catalog.Database.OpenConnection();
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
                    var type = catalog.Set<ItemType>().Single(t => t.Id == ship.TypeId);
                    int system = position.SystemId ?? catalog.Set<Station>().Single(s => s.Id == ship.StationId).SolarSystemId;
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

    private void ClearSimulation()
    {
        _acquisitionShips.Clear();
        _systemNpcs.Clear();
        _populationDue.Clear();
        _effectiveNpcs.Clear();
        _positions.Clear();
        _savedPositions.Clear();
        _dirtyPositions.Clear();
        _simulationProfiles = new();
    }
}
