using Eden.Data.Catalog;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Data.World.Propulsion;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
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
    private readonly ResidentMap<int, IReadOnlyList<NpcSimulationContact>> _simulationContacts = [];
    private readonly ResidentMap<int, long> _populationDue = [];
    private int? _populationRefreshSystem;

    private void InvalidatePopulationDeadline(int system, string reason)
    {
        if (_populationDue.Remove(system))
        {
            owner.CountNpcPopulation(reason);
        }
    }

    private void InvalidatePopulationDeadlines(string reason)
    {
        owner.CountNpcPopulation(reason, _populationDue.Count);
        _populationDue.Clear();
    }

    private static bool IsPublicAsteroidChange(CombatRowChange change) =>
        (change.Table == "WorldAsteroidGeometry" || change.Table == "WorldAsteroid" && !change.Resident)
        && !MissionMiningRules.IsDeposit(change.Id);

    private void ObserveAsteroidPopulation(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_populationDue.Count == 0 || !database.Settings.NpcPopulation.Mining)
        {
            return;
        }
        if (_populationRefreshSystem is { } scoped)
        {
            InvalidatePopulationDeadline(scoped, "asteroid");
            return;
        }
        foreach (var change in changes)
        {
            if (change.Table == "WorldAsteroidGeometry" && IsPublicAsteroidChange(change) && change.System is int knownSystem)
            {
                InvalidatePopulationDeadline(knownSystem, "asteroid");
            }
        }
        var ids = changes.Where(change => !change.Resident && IsPublicAsteroidChange(change)).Select(change => change.Id).ToArray();
        if (ids.Length == 0)
        {
            return;
        }
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
            InvalidatePopulationDeadlines("asteroid removed");
        }
        else
        {
            foreach (int system in systems.Distinct())
            {
                InvalidatePopulationDeadline(system, "asteroid");
            }
        }
    }

    private void InvalidatePopulation(int system, string reason)
    {
        InvalidatePopulationDeadline(system, reason);
        _systemNpcs.Remove(system);
        _beltClearances.Remove(system);
        _simulationContacts.Remove(system);
        _contacts.Remove(system);
        _npcSceneIndices.Remove(system);
        foreach (var key in _guardGroups.Keys.Where(k => k.System == system).ToArray())
        {
            _guardGroups.Remove(key);
        }
    }

    private void ObserveNpcPopulation(long id, string reason, bool resident)
    {
        if (_populationRefreshSystem is { } scoped)
        {
            InvalidatePopulation(scoped, reason);
            return;
        }
        if (_npcs.TryGetValue(id, out var previous))
        {
            InvalidatePopulation(previous.SystemId, reason);
        }
        int? current =
            resident || owner.Economy.IsLoaded
                ? owner.Economy.NpcSystem(id)
                : Loading(() =>
                {
                    using WorldDbContext world = new(database.Path) { IsResidentProjection = true };
                    return world.Set<WorldNpc>().Where(n => n.Id == id).Select(n => (int?)n.SystemId).SingleOrDefault();
                });
        if (current is int system)
        {
            InvalidatePopulation(system, reason);
        }
    }

    private void ObserveEncounterPopulation(long id, string reason, bool resident)
    {
        // Encounters and operations schedule population work. NPC and mining member
        // rows own the contacts and invalidate them when a refresh changes those rows.
        if (_populationRefreshSystem is { } scoped)
        {
            InvalidatePopulationDeadline(scoped, reason);
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
            InvalidatePopulationDeadline(system, reason);
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
                owner.Economy.Inspect(world =>
                    NpcPopulationMember.InSystem(world, system).Where(n => n.Active).Select(n => n.Id).Order().ToArray()
                )
            );
            _systemNpcs[system] = ids;
        }
        if (ids.Length == 0)
        {
            return [];
        }
        List<long>? missing = null;
        foreach (long id in ids)
        {
            if (!_npcs.ContainsKey(id) || _staleNpcs.Contains(id))
            {
                (missing ??= []).Add(id);
            }
        }
        if (missing is not null)
        {
            LoadNpcs(missing, now);
        }
        var current = new LiveCombatNpc[ids.Length];
        int count = 0;
        foreach (long id in ids)
        {
            if (Npc(id, now) is { Available: true } npc && npc.SystemId == system)
            {
                current[count++] = npc;
            }
        }
        return count == current.Length ? current : current[..count];
    }

    private IReadOnlyDictionary<long, IReadOnlyDictionary<DogmaAttributeId, double>> Effective(LiveCombatNpc[] npcs)
    {
        List<LiveCombatNpc>? missing = null;
        foreach (var npc in npcs)
        {
            if (!_effectiveNpcs.ContainsKey(npc.Id))
            {
                (missing ??= []).Add(npc);
            }
        }
        if (missing is not null)
        {
            Loading(() =>
            {
                void Apply(ILookup<long, WorldTackle> effects)
                {
                    foreach (var npc in missing)
                    {
                        _effectiveNpcs[npc.Id] = TackleDefinitions.NpcAttributes(
                            npc.Attributes,
                            effects[npc.Id].Select(t => t.Module()).ToArray()
                        );
                    }
                }
                return owner.Economy.Inspect(world =>
                {
                    Apply(PilotTackleStore.NpcTackles(world, missing.Select(n => n.Id).ToArray()));
                    return true;
                });
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
        HashSet<long> interdicted = [];
        if (ships.Any(ship => ship.Navigation.Warp is null && ship.Navigation.WarpDestination is not null))
        {
            var fields = new PilotInterdictionStore(database);
            ships = ships
                .Select(ship =>
                {
                    if (ship.Navigation.Warp is not null || ship.Navigation.WarpDestination is not { } destination)
                    {
                        return ship;
                    }
                    var outcome = fields.Resolve(ship.Id, system, ship.Position, destination, now);
                    var navigation = ship.Navigation.Copy();
                    if (outcome.Blocked)
                    {
                        navigation.CancelAlignment();
                        navigation.Stop();
                        interdicted.Add(ship.Id);
                    }
                    return ship with { Navigation = navigation };
                })
                .ToArray();
        }
        var result = LiveSystemMotionRules.Advance(stamp, now, ships, objects, npcs, Aggression(), Effective(npcs));
        if (interdicted.Count > 0)
        {
            result = result with
            {
                Ships = result
                    .Ships.Select(ship =>
                        interdicted.Contains(ship.Ship.Id)
                            ? ship with
                            {
                                Changes = ship.Changes.Append(new NavigationChange.Stopped()).ToArray(),
                            }
                            : ship
                    )
                    .ToArray(),
            };
        }
        // Load saved position identity before publishing the staged simulation.
        var missing = ships.Where(s => !_positions.ContainsKey(s.Id)).Select(s => s.Id).ToArray();
        if (missing.Length != 0)
        {
            foreach (long id in missing)
            {
                var row = owner.Economy.Peek<Eden.Data.World.Docking.FlightPosition>(id);
                _savedPositions[id] = row is null ? null : new(row.SystemId, new(row.X, row.Y, row.Z));
            }
        }
        var warping = result
            .Ships.Where(r => r.Ship.Navigation.Warp is not null && ships.Single(s => s.Id == r.Ship.Id).Navigation.Warp is null)
            .ToArray();
        if (warping.Length != 0)
        {
            using CatalogDbContext catalog = new(database.CatalogPath);
            owner.Economy.Read(
                world =>
                {
                    foreach (var ship in warping)
                    {
                        var pilot = world.Pilots.SingleOrDefault(p => p.ActiveShipId == ship.Ship.Id);
                        if (pilot is not null)
                        {
                            PilotTaskTrackingStore.Record(world, catalog, pilot.Id, now, TaskEvents.Warped);
                        }
                    }
                    world.SaveChanges();
                    return true;
                },
                new(ships: warping.Select(ship => ship.Ship.Id))
            );
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
            owner.Economy.MissileHistory(npc.Id)?.Observe(now, npc.Position, npc.Velocity);
        }
        foreach (var player in result.Ships)
        {
            long id = player.Ship.Id;
            PublishPilotPosition(id, system, player.Ship.Position);
            owner.Economy.MissileHistory(id)?.Observe(now, player.Ship.Position, player.Ship.Navigation.Velocity);
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
        // A queued frame or disconnect can arrive after the hull was destroyed.
        // Its last motion must not recreate a position for a deleted ship.
        if (owner.Economy.IsLoaded && !owner.Economy.IsShip(id))
        {
            _positions.Remove(id);
            _savedPositions.Remove(id);
            _dirtyPositions.Remove(id);
            return;
        }
        owner.TrackShip(id, system);
        bool fieldChanged = false;
        if (Eden.Gameplay.Exploration.AbyssalRules.IsSystem(system) && _positions.TryGetValue(id, out var oldPosition))
        {
            fieldChanged = owner.Economy.Inspect(world =>
            {
                var run = world
                    .Filter<Eden.Data.World.Exploration.SavedAbyssalRun>(r =>
                        r.SystemId == system && r.Status == (int)Eden.Gameplay.Exploration.AbyssalStatus.Active
                    )
                    .SingleOrDefault();
                return run is not null
                    && Eden.Gameplay.Exploration.AbyssalEnvironmentRules.Fields(
                        Eden.Gameplay.Exploration.AbyssalContent.RoomAt(oldPosition.Position),
                        run.Seed,
                        oldPosition.Position
                    )
                        != Eden.Gameplay.Exploration.AbyssalEnvironmentRules.Fields(
                            Eden.Gameplay.Exploration.AbyssalContent.RoomAt(position),
                            run.Seed,
                            position
                        );
            });
        }
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
        if (fieldChanged)
        {
            // Project this crossing once so the ordinary fit revision publishes to every observer.
            owner.Economy.Read(_ => true, new(ships: [id]));
        }
    }

    private LiveCombatNpc PublishNpc(LiveCombatNpc npc)
    {
        var previous = _npcs[npc.Id];
        if (
            npc.AnchorKind == NpcAnchorKind.Abyssal
            && owner.Economy.Peek<Eden.Data.World.Exploration.SavedAbyssalRun>(npc.EncounterId) is { } run
            && Eden.Gameplay.Exploration.AbyssalEnvironmentRules.Fields(
                Eden.Gameplay.Exploration.AbyssalContent.RoomAt(previous.Position),
                run.Seed,
                previous.Position
            )
                != Eden.Gameplay.Exploration.AbyssalEnvironmentRules.Fields(
                    Eden.Gameplay.Exploration.AbyssalContent.RoomAt(npc.Position),
                    run.Seed,
                    npc.Position
                )
        )
        {
            npc = RefreshAbyssalNpc(npc, run);
            _effectiveNpcs.Remove(npc.Id);
        }
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

    private LiveCombatNpc RefreshAbyssalNpc(LiveCombatNpc npc, Eden.Data.World.Exploration.SavedAbyssalRun run)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var profile = _simulationProfiles.Read(catalog, npc.TypeId);
        var attributes = PlayerEwarRules.NpcAttributes(
            Eden.Gameplay.Exploration.AbyssalEnvironmentRules.Apply(
                profile.Attributes,
                Eden.World.Exploration.AbyssalEnvironmentStore.At(run, npc.Position)
            ),
            npc.IncomingElectronicWarfare,
            npc.MissileRange
        );
        return npc with
        {
            Attributes = attributes,
            SignatureRadius = attributes.GetValueOrDefault(DogmaAttributeId.SignatureRadius),
            RepairProfile = NpcRepairRules.Profile(attributes),
        };
    }

    private LiveCombatShip Positioned(LiveCombatShip ship) =>
        _positions.TryGetValue(ship.ShipId, out var position)
            ? ship with
            {
                Position = position.Position,
                SystemId = position.System ?? ship.SystemId,
            }
            : ship;

    private LiveCombatNpc[] AdvanceSimulationNpcs(int system, long now, Func<long> refreshPopulation)
    {
        bool scheduled = _populationDue.TryGetValue(system, out long due);
        if (!scheduled || now >= due)
        {
            owner.CountNpcPopulation(scheduled ? "deadline reached" : "deadline missing");
            int? previousScope = _populationRefreshSystem;
            _populationRefreshSystem = system;
            try
            {
                _populationDue[system] = refreshPopulation();
            }
            finally
            {
                _populationRefreshSystem = previousScope;
            }
        }
        var npcs = SystemNpcs(system, now);
        if (npcs.Length == 0)
        {
            return npcs;
        }
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
            if (!ReferenceEquals(npc, npcs[index]))
            {
                npcs[index] = PublishNpc(npc);
            }
        }
        return npcs;
    }

    private sealed record BeltClearance(long[] Members, long Contacts, long Geometry, SpacePosition Center, double Range);

    private readonly ResidentMap<int, BeltClearance> _beltClearances = [];
    private long _miningGeometry;

    // The encounter owner invalidates these membership and population snapshots on changes.
    // Cold, mobile and contested scenes return to the ordinary NPC and defense owners.
    internal bool QuietMiningBelt(int system, long ship, SpacePosition position, double radius, long now, out bool populationDue)
    {
        using var ownership = owner.Enter();
        if (!_populationDue.TryGetValue(system, out long due) || due <= now || !_systemNpcs.TryGetValue(system, out var members))
        {
            populationDue = true;
            return false;
        }
        populationDue = false;
        if (
            !simulationObjects
            || !CanRun
            || !_droneAttackers.TryGetValue(ship, out var attacks)
            || attacks.Length != 0
            || _residentAttackers.TryGetValue(ship, out var pending) && pending.Count != 0
            || DroneControllers().ContainsKey(ship)
            || owner.Reads is not { } reads
            || reads.HasNpcEwar(ship)
            || reads.Incoming(ship).Count != 0
        )
        {
            return false;
        }
        var aggressions = Aggression();
        if (aggressions.Count > 0)
        {
            var recent = DroneTriggers(ship).Incoming;
            if (recent.At > 0 && recent.At + CrimewatchRules.PvpDuration.Ticks > now && aggressions.ContainsKey(recent.OtherId))
            {
                return false;
            }
        }
        if (aggressions.Values.Any(aggression => aggression.ShipId == ship))
        {
            return false;
        }
        if (
            _staleNpcs.Count == 0
            && _beltClearances.TryGetValue(system, out var cleared)
            && ReferenceEquals(cleared.Members, members)
            && cleared.Contacts == _contactEpoch
            && cleared.Geometry == _miningGeometry
            && cleared.Center.DistanceTo(position) + radius < cleared.Range
        )
        {
            return true;
        }
        double clearance = double.PositiveInfinity;
        foreach (long id in members)
        {
            if (
                _staleNpcs.Contains(id)
                || !_npcs.TryGetValue(id, out var npc)
                || npc.Transit is not null
                || npc.Navigation is NpcNavigationState.Pursuing or NpcNavigationState.Orbiting or NpcNavigationState.Returning
            )
            {
                return false;
            }
            // Ambient stationary traffic cannot acquire a miner. A previously acquired target,
            // including guard retaliation, was checked against the aggression owner above.
            if (npc.Role == NpcRole.Pirate)
            {
                clearance = Math.Min(clearance, position.DistanceTo(npc.Position) - npc.Radius - NpcEncounterDefinitions.VisibilityRange);
            }
            if (clearance <= radius)
            {
                return false;
            }
        }
        _beltClearances[system] = new(members, _contactEpoch, _miningGeometry, position, clearance);
        return true;
    }

    internal IReadOnlyList<NpcSimulationContact>? Simulate(int system, long now, Func<long> refreshPopulation)
    {
        return Execute(() => SimulateOwned(system, now, refreshPopulation), nameof(Simulate));
    }

    private IReadOnlyList<NpcSimulationContact>? SimulateOwned(int system, long now, Func<long> refreshPopulation)
    {
        using var ownership = owner.Enter();
        if (!simulationObjects || !CanRun)
        {
            return null;
        }
        Trim();
        var npcs = AdvanceSimulationNpcs(system, now, refreshPopulation);
        if (npcs.Length == 0)
        {
            return [];
        }
        if (_simulationContacts.TryGetValue(system, out var contacts) && contacts.Count == npcs.Length)
        {
            int matching = 0;
            while (matching < npcs.Length)
            {
                var npc = npcs[matching];
                var contact = contacts[matching];
                if (contact.Id != npc.Id || contact.Position != npc.Position || contact.Warping != (npc.Transit is not null))
                {
                    break;
                }
                matching++;
            }
            if (matching == npcs.Length)
            {
                return contacts;
            }
        }
        contacts = Array.AsReadOnly(npcs.Select(n => new NpcSimulationContact(n.Id, n.Position, n.Transit is not null)).ToArray());
        _simulationContacts[system] = contacts;
        return contacts;
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

    private IReadOnlyList<NpcContact> SimulationContacts(int system, long now, Func<long> refreshPopulation, SpacePosition? position = null)
    {
        var npcs = AdvanceSimulationNpcs(system, now, refreshPopulation);
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
        decimal bountyMultiplier = database.Settings.Rewards.BountyMultiplier;
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
                BountyCents = WorldTuningRules.Reward(NpcBountyRules.BaseCents(npc.Role, npc.Attributes), bountyMultiplier),
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
        var npcs =
            ships.Length == 1
                ? SystemNpcs(ships[0].SystemId, now)
                : ships.Select(s => s.SystemId).Distinct().SelectMany(system => SystemNpcs(system, now)).ToArray();
        var additions = LiveNpcAcquisitionRules.Acquire(
            npcs,
            ships.Select(s => new LiveNpcTarget(s.ShipId, s.SystemId, s.Position, s.Radius)).ToArray(),
            aggression.ContainsKey,
            now
        );
        foreach (var addition in additions)
        {
            aggression[addition.NpcId] = addition;
            _dirtyAggressions.Add(addition.NpcId);
            owner.ObjectsChanged();
        }
    }

    private sealed record AcquisitionIdentity(long PilotId, long ShipId);

    private sealed record AcquisitionShip(long PilotId, long ShipId, int SystemId, SpacePosition Position, double Radius, int GroupId);

    private readonly ResidentMap<long, AcquisitionShip?> _acquisitionShips = [];

    private IEnumerable<AcquisitionShip> AcquisitionShips(IReadOnlyList<NpcTargetCandidate> candidates)
    {
        var missing = candidates.Select(c => c.PilotId).Distinct().Where(id => !_acquisitionShips.ContainsKey(id)).ToArray();
        if (missing.Length != 0)
        {
            Loading(() =>
            {
                using CatalogDbContext catalog = new(database.CatalogPath);
                var loaded = missing.ToDictionary(id => id, _ => (AcquisitionShip?)null);
                owner.Economy.Inspect(world =>
                {
                    var pilots = world
                        .Pilots.Where(p => Enumerable.Contains(missing, p.Id))
                        .Select(p => new AcquisitionIdentity(p.Id, p.ActiveShipId))
                        .ToArray();
                    foreach (var pilot in pilots)
                    {
                        var ship = world.Ships.Find(pilot.ShipId);
                        if (ship?.Position is not { } position)
                        {
                            continue;
                        }
                        var type = catalog.RequireItem(ship.TypeId);
                        int system = position.SystemId ?? catalog.RequireStation(ship.StationId).SolarSystemId;
                        loaded[pilot.PilotId] = new(
                            pilot.PilotId,
                            ship.Id,
                            system,
                            new(position.X, position.Y, position.Z),
                            type.Radius ?? 0,
                            type.GroupId
                        );
                    }
                    return true;
                });
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
        _simulationContacts.Clear();
        _beltClearances.Clear();
        InvalidatePopulationDeadlines("runtime reset");
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
