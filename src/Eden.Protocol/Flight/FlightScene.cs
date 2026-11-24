using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Protocol.Targeting;
using ZLinq;

namespace Eden.Protocol.Flight;

internal readonly record struct FlightSceneContext(
    DockedPilot Pilot,
    bool InBallpark,
    bool CanTarget,
    int SimulatedStamp,
    StaticSpaceObject? Observer = null
)
{
    internal SpacePosition? Position => Observer?.Position ?? Pilot.Position;
    internal long EntityId => Observer?.Id ?? Pilot.ShipId;
}

internal sealed class FlightScene(
    SharedSpace space,
    TimeProvider clock,
    IReadOnlyList<StaticSpaceObject> objects,
    FlightEffects effects,
    Action<DestinyCommand> update,
    Action<StampedDestinyUpdate> send,
    IPilotHealth? health,
    INpcEncounters? encounters,
    IWrecks? wrecks,
    IPilotMissiles? missiles,
    IPilotDrones? drones,
    IPilotCommandBursts? commandBursts = null,
    ICooperativeFleets? fleets = null
)
{
    private const long GateArrivalWindow = FlightTravel.ArrivalDurationTicks * TimeSpan.TicksPerSecond;
    private const long GateDepartureWindow = FlightTravel.JumpDurationTicks * TimeSpan.TicksPerSecond;
    private FlightNpcScene? _npcScene;
    private FlightDroneScene? _droneScene;
    private ShipContact[] _peers = [];
    private readonly HashSet<long> _cloakedPeers = [];
    private readonly Dictionary<long, long> _peerOrderSequences = new();
    private readonly Dictionary<long, long> _gateArrivals = new();
    private readonly Dictionary<long, long> _gateDepartures = new();
    private long? _observingSince;
    private long _latestGateObservation;
    private StaticSpaceObject[] _visibleAsteroids = [];
    private IReadOnlyList<StaticSpaceObject>? _asteroidSource;
    private IReadOnlyList<StaticSpaceObject> _objects = objects;
    private IReadOnlyList<StaticSpaceObject> _anomalies = [];
    private IReadOnlyList<StaticSpaceObject> _structures = [];
    private WreckContact[] _wrecks = [];
    private MissileContact[] _missiles = [];
    private DoomsdayFieldContact[] _doomsdayFields = [];

    private FlightNpcScene Npcs => _npcScene ??= new(space, clock, encounters, effects, update, send);
    private FlightDroneScene Drones => _droneScene ??= new(space, clock, drones, update);

    internal IReadOnlyList<StaticSpaceObject> VisibleAsteroids => _visibleAsteroids;

    internal IReadOnlyList<StaticSpaceObject> Objects
    {
        get => _objects;
        private set
        {
            _objects = value;
            _asteroidSource = null;
        }
    }
    internal IReadOnlyList<NpcContact> VisibleNpcs => Npcs.Contacts;
    internal IReadOnlyList<WreckContact> VisibleWrecks => _wrecks;
    internal IReadOnlyList<MissileContact> VisibleMissiles => _missiles;
    internal IReadOnlyList<ShipContact> VisiblePeers => _peers;
    internal IReadOnlyList<DroneContact> VisibleDrones => Drones.Contacts;
    internal (int Arrivals, int Departures, int Cloaks) RetainedPeerEvents =>
        (_gateArrivals.Count, _gateDepartures.Count, _cloakedPeers.Count);

    internal IEnumerable<BallMotion> DiagnosticMotion(FlightSceneContext context) =>
        VisibleObjects(context)
            .Select(body => new BallMotion(body.Id, body.Position, SpacePosition.Zero))
            .Concat(
                _peers
                    .Where(peer => peer.Pilot.Position is not null)
                    .Select(peer => new BallMotion(peer.Pilot.ShipId, peer.Pilot.Position!, peer.Velocity))
            )
            .Concat(Npcs.Contacts.Select(npc => new BallMotion(npc.Id, npc.Position, npc.Navigation?.Velocity ?? SpacePosition.Zero)))
            .Concat(Drones.Contacts.Select(drone => new BallMotion(drone.Id, drone.Position, drone.Velocity)))
            .Concat(_wrecks.Select(wreck => new BallMotion(wreck.Id, wreck.Position, SpacePosition.Zero)))
            .Concat(_missiles.Select(missile => new BallMotion(missile.Id, missile.Flight.Position, missile.Flight.Velocity)));

    internal void RefreshNpcs(FlightSceneContext context) => Npcs.RefreshNpcs(context);

    internal void RefreshDrones(FlightSceneContext context) => Drones.RefreshDrones(context);

    internal void ResetStructureObservers()
    {
        _peers = [];
        _peerOrderSequences.Clear();
        _cloakedPeers.Clear();
        _gateArrivals.Clear();
        _gateDepartures.Clear();
        Npcs.Clear();
        Drones.Reset();
    }

    internal void NotifyNpcWeapon(long npcId, NpcWeaponContact weapon) => Npcs.NotifyNpcWeapon(npcId, weapon);

    internal void NotifyHealth(ShipHealth current) => Npcs.NotifyHealth(current);

    internal void StopNpcWeapons(long destroyed) => Npcs.StopNpcWeapons(destroyed);

    internal bool NpcInWarp(long id) => Npcs.InWarp(id);

    internal void ReplaceObjects(IReadOnlyList<StaticSpaceObject> destination) => Objects = destination;

    internal void ForgetMissiles()
    {
        foreach (var missile in _missiles.Where(m => !m.Flight.Orphaned))
        {
            update(new DestinyCommand.AttackIndicator(missile.SourceShipId, missile.Id, missile.TargetId, false));
        }
        _missiles = [];
    }

    internal void LeaveSpace()
    {
        Objects = Objects.ExceptBy(_anomalies.Select(o => o.Id), o => o.Id).ToArray();
        _anomalies = [];
        if (_doomsdayFields.Length != 0)
        {
            Objects = Objects.Where(body => body.TypeId != DoomsdayFieldRules.BeaconType).ToArray();
            _doomsdayFields = [];
        }
        _peers = [];
        _peerOrderSequences.Clear();
        _cloakedPeers.Clear();
        _gateArrivals.Clear();
        _gateDepartures.Clear();
        _observingSince = null;
        _latestGateObservation = 0;
        _visibleAsteroids = [];
        ForgetMissiles();
        Npcs.Clear();
        _wrecks = [];
    }

    internal TargetContact? FindTarget(FlightSceneContext context, long id)
    {
        if (!context.CanTarget)
        {
            return null;
        }
        if (space.TetherProtected(id))
        {
            return null;
        }
        if (space.FindTargetablePeer(context.Pilot.SystemId, context.EntityId, context.Position, id) is { } ship)
        {
            return new(id, ship.Pilot.ShipTypeId, ship.Pilot.Position!, ship.Pilot.Flight.ShipRadius);
        }
        if (Npcs.Contacts.FirstOrDefault(n => n.Id == id && n.Warp is null && n.Role != NpcRole.Sentry) is { } npc)
        {
            return new(id, npc.TypeId, npc.Position, npc.Radius);
        }
        if (Drones.Contacts.FirstOrDefault(d => d.Id == id) is { } drone)
        {
            return new(id, drone.Profile.TypeId, drone.Position, drone.Profile.Radius);
        }
        if (_wrecks.FirstOrDefault(w => w.Id == id) is { } wreck)
        {
            return new(id, wreck.TypeId, wreck.Position, wreck.Radius);
        }
        return
            VisibleObjects(context)
                .FirstOrDefault(o =>
                    o.Id == id
                    && (
                        o.Structure is { } structure && Eden.Gameplay.Structures.StructureCombatRules.Vulnerable(structure.State)
                        || MiningDefinitions.IsResource(o.CategoryId, o.GroupId)
                        || o.GroupId == Eden.Gameplay.Sovereignty.SovereigntyRules.HubGroupId
                        || o.GroupId == Eden.Gameplay.Sovereignty.SovereigntyWarfareRules.CommandNodeGroupId
                        || o.CynoDeployable is not null
                        || Eden.Gameplay.Exploration.HackingRules.IsContainer(o.Id)
                    )
                )
                is { } targetObject
            ? new(id, targetObject.TypeId, targetObject.Position, targetObject.Radius)
            : null;
    }

    internal PilotWeaponTarget? FindPilotWeaponTarget(FlightSceneContext context, long id) =>
        context.CanTarget
        && !space.TetherProtected(id)
        && space.FindTargetablePeer(context.Pilot.SystemId, context.EntityId, context.Position, id) is { } ship
            ? new(id, ship.Velocity)
            : null;

    private static bool IsPublicAsteroid(StaticSpaceObject item) =>
        MiningDefinitions.IsResource(item.CategoryId, item.GroupId) && !MissionMiningRules.IsDeposit(item.Id);

    private StaticSpaceObject[] VisibleObjects(FlightSceneContext context) =>
        Objects
            .AsValueEnumerable()
            .Where(o =>
                !MiningDefinitions.IsResource(o.CategoryId, o.GroupId)
                || (context.Position is { } position && position.DistanceTo(o.Position) <= AsteroidDefinitions.VisibilityRange)
            )
            .ToArray();

    private MissileContact[] ReadMissiles(FlightSceneContext context) =>
        context.Position is not { } position
            ? []
            : (missiles?.List(context.Pilot.SystemId) ?? [])
                .Where(m => position.DistanceTo(m.Flight.Position) <= NpcEncounterDefinitions.VisibilityRange)
                .ToArray();

    internal void RefreshMissiles(FlightSceneContext context)
    {
        if (!context.InBallpark)
        {
            return;
        }
        var current = ReadMissiles(context);
        foreach (var departed in _missiles.ExceptBy(current.Select(m => m.Id), m => m.Id))
        {
            update(new DestinyCommand.AttackIndicator(departed.SourceShipId, departed.Id, departed.TargetId, false));
            update(new DestinyCommand.RemoveBall(departed.Id, missiles?.Impacted(departed.Id) == true));
        }
        var added = current.ExceptBy(_missiles.Select(m => m.Id), m => m.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddMissiles(added, space.Stamp(clock))));
            foreach (var missile in added.Where(m => !m.Flight.Orphaned))
            {
                update(new DestinyCommand.AttackIndicator(missile.SourceShipId, missile.Id, missile.TargetId, true));
            }
        }
        foreach (var missile in current)
        {
            var previous = _missiles.FirstOrDefault(m => m.Id == missile.Id);
            if (previous is not null && previous.Flight.ElapsedSeconds != missile.Flight.ElapsedSeconds)
            {
                var position = missile.Flight.Position;
                var velocity = missile.Flight.Velocity;
                update(new DestinyCommand.SetBallPosition(missile.Id, position.X, position.Y, position.Z));
                update(new DestinyCommand.SetBallVelocity(missile.Id, velocity.X, velocity.Y, velocity.Z));
            }
            if (previous is not null && missile.Flight.Orphaned && !previous.Flight.Orphaned)
            {
                update(new DestinyCommand.AttackIndicator(missile.SourceShipId, missile.Id, missile.TargetId, false));
                var point = missile.Flight.Goto;
                update(new DestinyCommand.GotoPoint(missile.Id, point.X, point.Y, point.Z));
            }
        }
        _missiles = current;
    }

    internal StaticSpaceObject? NavigationTarget(FlightSceneContext context, long id)
    {
        if (id == context.Pilot.ShipId || context.Position is not { } position)
        {
            return null;
        }
        var target =
            VisibleObjects(context).FirstOrDefault(o => o.Id == id)
            ?? (
                space
                    .Peers(context.Pilot)
                    .FirstOrDefault(s => s.Pilot.ShipId == id && s.Warp is null && s.AlignTo is null && s.DepartureGate is null)
                    is { } peer
                    ? new StaticSpaceObject(
                        peer.Pilot.ShipId,
                        peer.Pilot.ShipTypeId,
                        peer.Pilot.ShipGroupId,
                        peer.Pilot.ShipCategoryId,
                        checked((int)peer.Pilot.PilotId),
                        peer.Pilot.Position!,
                        peer.Pilot.Flight.ShipRadius
                    )
                    : null
            )
            ?? (
                Npcs.Contacts.FirstOrDefault(n => n.Id == id) is { } npc
                    ? new(npc.Id, npc.TypeId, npc.GroupId, npc.CategoryId, npc.CorporationId, npc.Position, npc.Radius)
                : Drones.Contacts.FirstOrDefault(d => d.Id == id) is { } drone
                    ? new(
                        drone.Id,
                        drone.Profile.TypeId,
                        DroneRules.CombatDroneGroup,
                        (int)InventoryCategory.Drone,
                        checked((int)drone.PilotId),
                        drone.Position,
                        drone.Profile.Radius
                    )
                : _wrecks.FirstOrDefault(w => w.Id == id) is { } wreck
                    ? new(
                        wreck.Id,
                        wreck.TypeId,
                        wreck.GroupId,
                        wreck.CategoryId,
                        checked((int)wreck.OwnerId),
                        wreck.Position,
                        wreck.Radius
                    )
                : null
            );
        return target is not null && position.DistanceTo(target.Position) <= NavigationDefinitions.MaximumTargetDistance ? target : null;
    }

    private WreckContact[] ReadWrecks(FlightSceneContext context) =>
        context.Position is not { } position
            ? []
            : (wrecks?.List(context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? [])
                .Where(w =>
                    w.SystemId == context.Pilot.SystemId
                    && (w.PlanetId.HasValue || position.DistanceTo(w.Position) <= WreckDefinitions.VisibilityRange)
                )
                .Select(wreck => WithLootRights(context, wreck))
                .ToArray();

    private WreckContact WithLootRights(FlightSceneContext context, WreckContact wreck) =>
        wreck.PlanetId is null
        && context.Pilot.Identity.Fleet is { FleetId: var fleet }
        && fleets?.CanShareLoot(context.Pilot.PilotId, wreck.OwnerId, wreck.SystemId) == true
            ? wreck with
            {
                LootFleetId = fleet,
            }
            : wreck;

    internal void AddWreck(FlightSceneContext context, WreckContact contact)
    {
        if (context.InBallpark && contact.SystemId == context.Pilot.SystemId && !_wrecks.Any(wreck => wreck.Id == contact.Id))
        {
            _wrecks = [.. _wrecks, contact];
            update(new DestinyCommand.AddBalls(Ballpark.AddWrecks([contact], space.Stamp(clock))));
        }
    }

    internal void RefreshWrecks(FlightSceneContext context)
    {
        if (!context.InBallpark)
        {
            return;
        }
        var current = ReadWrecks(context);
        foreach (var departed in _wrecks.ExceptBy(current.Select(w => w.Id), w => w.Id))
        {
            update(new DestinyCommand.RemoveBall(departed.Id, false));
        }
        var added = current.ExceptBy(_wrecks.Select(w => w.Id), w => w.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddWrecks(added, space.Stamp(clock))));
        }
        foreach (var wreck in current)
        {
            if (_wrecks.FirstOrDefault(w => w.Id == wreck.Id) is { } previous && previous != wreck)
            {
                if (previous.TypeId != wreck.TypeId)
                {
                    update(new DestinyCommand.RemoveBall(wreck.Id, false));
                    update(new DestinyCommand.AddBalls(Ballpark.AddWrecks([wreck], space.Stamp(clock))));
                }
                else
                {
                    update(new DestinyCommand.OnSlimItemChange(wreck.Id, Ballpark.WreckSlim(wreck)));
                    if (previous.Position != wreck.Position)
                    {
                        update(new DestinyCommand.SetBallPosition(wreck.Id, wreck.Position.X, wreck.Position.Y, wreck.Position.Z));
                    }
                }
            }
        }
        _wrecks = current;
    }

    internal void RefreshAnomalies(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> objects)
    {
        if (_anomalies.SequenceEqual(objects))
        {
            return;
        }
        var previous = _anomalies;
        Objects = Objects.ExceptBy(previous.Select(o => o.Id), o => o.Id).Concat(objects).ToArray();
        _anomalies = objects;
        if (!context.InBallpark)
        {
            return;
        }
        foreach (var removed in previous.ExceptBy(objects.Select(o => o.Id), o => o.Id))
        {
            update(new DestinyCommand.RemoveBall(removed.Id, false));
        }
        var before = previous.ToDictionary(o => o.Id);
        foreach (var changed in objects.Where(o => before.TryGetValue(o.Id, out var old) && old != o))
        {
            update(new DestinyCommand.OnSlimItemChange(changed.Id, new StaticSlimItem(changed)));
        }
        var added = objects.ExceptBy(previous.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, space.Stamp(clock))));
        }
    }

    internal void RefreshAsteroids(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> asteroids)
    {
        // Resident fields publish immutable lists; scene replacement clears the cached source.
        if (!ReferenceEquals(_asteroidSource, asteroids))
        {
            Objects = Objects.Where(o => !IsPublicAsteroid(o)).Concat(asteroids).ToArray();
            _asteroidSource = asteroids;
        }
        // Movement can cross visibility boundaries even when the field is unchanged.
        RefreshVisibleAsteroids(context);
    }

    internal void RefreshStructures(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> structures)
    {
        if (ReferenceEquals(_structures, structures) || _structures.SequenceEqual(structures))
        {
            return;
        }
        var previous = Objects.Where(o => o.Structure is not null).ToArray();
        Objects = Objects.Where(o => o.Structure is null).Concat(structures).ToArray();
        _structures = structures;
        if (!context.InBallpark)
        {
            return;
        }
        foreach (var removed in previous.ExceptBy(structures.Select(o => o.Id), o => o.Id))
        {
            update(
                new DestinyCommand.RemoveBall(removed.Id, removed.Structure?.State != Eden.Gameplay.Structures.StructureState.Unanchored)
            );
        }
        foreach (var changed in structures.Where(o => previous.FirstOrDefault(p => p.Id == o.Id) is { } old && old != o))
        {
            update(new DestinyCommand.OnSlimItemChange(changed.Id, new StaticSlimItem(changed)));
            if (Ballpark.StructureDamage(changed) is { } damage)
            {
                update(new DestinyCommand.OnDamageStateChange(changed.Id, damage));
            }
        }
        var added = structures.ExceptBy(previous.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, space.Stamp(clock))));
        }
    }

    internal void RefreshDoomsdayFields(FlightSceneContext context, IReadOnlyList<DoomsdayFieldContact> current)
    {
        if (!context.InBallpark || context.Pilot.Position is not { } observer)
        {
            return;
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var visible = current
            .Where(field =>
                field.VisualAt <= now
                && field.VisualEndsAt > now
                && field.SourcePosition.DistanceTo(observer) <= NpcEncounterDefinitions.VisibilityRange
            )
            .ToArray();
        if (_doomsdayFields.SequenceEqual(visible))
        {
            return;
        }
        var removed = _doomsdayFields.ExceptBy(visible.Select(field => field.Id), field => field.Id).ToArray();
        var added = visible.ExceptBy(_doomsdayFields.Select(field => field.Id), field => field.Id).ToArray();
        _doomsdayFields = visible;
        Objects = Objects
            .Where(body => body.TypeId != DoomsdayFieldRules.BeaconType)
            .Concat(visible.Select(field => field.Beacon).OfType<StaticSpaceObject>())
            .ToArray();
        foreach (var field in removed)
        {
            DoomsdayFieldEffect(field, now, false);
            if (field.Beacon is { } oldBeacon)
            {
                update(new DestinyCommand.RemoveBall(oldBeacon.Id, false));
            }
        }
        var beacons = added.Select(field => field.Beacon).OfType<StaticSpaceObject>().ToArray();
        if (beacons.Length != 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(beacons, space.Stamp(clock))));
        }
        foreach (var field in added)
        {
            DoomsdayFieldEffect(field, now, true);
        }
    }

    private void DoomsdayFieldEffect(DoomsdayFieldContact field, long now, bool start)
    {
        Dictionary<string, object?>? graphic = null;
        double duration = 10_000;
        if (start && field.StartOffset is { } first && field.EndOffset is { } second)
        {
            double progress = Math.Clamp((now - field.VisualAt) / (double)(field.VisualEndsAt - field.VisualAt), 0, 1);
            var current = first + (second - first) * progress;
            duration = (field.VisualEndsAt - now) / (double)TimeSpan.TicksPerMillisecond;
            graphic = new()
            {
                ["startTargetOffset"] = new object?[] { current.X, current.Y, current.Z },
                ["endTargetOffset"] = new object?[] { second.X, second.Y, second.Z },
                ["duration"] = duration,
            };
        }
        update(
            new DestinyCommand.TimedSpecialEffect(
                field.ShipId,
                field.ModuleId,
                field.TypeId,
                field.Beacon?.Id,
                0,
                DoomsdayFieldRules.Graphic(field.TypeId),
                true,
                start,
                false,
                duration,
                field.VisualAt,
                graphic
            )
        );
    }

    internal void RefreshCynos(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> current)
    {
        static bool Cyno(StaticSpaceObject body) =>
            body.GroupId is CapitalTravelRules.FieldGroup or CapitalTravelRules.CovertFieldGroup || body.CynoDeployable is not null;
        var previous = Objects.Where(Cyno).ToArray();
        if (previous.SequenceEqual(current))
        {
            return;
        }
        Objects = Objects.Where(body => !Cyno(body)).Concat(current).ToArray();
        if (!context.InBallpark)
        {
            return;
        }
        foreach (var removed in previous.ExceptBy(current.Select(body => body.Id), body => body.Id))
        {
            update(new DestinyCommand.RemoveBall(removed.Id, false));
        }
        foreach (var changed in current)
        {
            if (
                previous.FirstOrDefault(prior => prior.Id == changed.Id)?.CynoDeployable is { } before
                && changed.CynoDeployable is { } after
            )
            {
                if (before.Active != after.Active)
                {
                    update(new DestinyCommand.OnSlimItemChange(changed.Id, new StaticSlimItem(changed)));
                }
                if (
                    before.DamagedAt != after.DamagedAt
                    || before.Health.Remaining.Armor != after.Health.Remaining.Armor
                    || before.Health.Remaining.Structure != after.Health.Remaining.Structure
                )
                {
                    NotifyHealth(after.Health);
                }
            }
        }
        var added = current.ExceptBy(previous.Select(body => body.Id), body => body.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, space.Stamp(clock))));
        }
    }

    internal void RefreshInterdiction(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> contacts)
    {
        var previous = Objects.Where(o => o.InterdictionRadius is not null).ToArray();
        if (previous.SequenceEqual(contacts))
        {
            return;
        }
        Objects = Objects.Where(o => o.InterdictionRadius is null).Concat(contacts).ToArray();
        if (!context.InBallpark)
        {
            return;
        }
        foreach (var removed in previous.ExceptBy(contacts.Select(o => o.Id), o => o.Id))
        {
            update(new DestinyCommand.RemoveBall(removed.Id, false));
        }
        foreach (var changed in contacts.Where(o => previous.FirstOrDefault(p => p.Id == o.Id) is { } old && old != o))
        {
            update(new DestinyCommand.OnSlimItemChange(changed.Id, new StaticSlimItem(changed)));
        }
        var added = contacts.ExceptBy(previous.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, space.Stamp(clock))));
        }
    }

    internal void RefreshAbyssal(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> contacts)
    {
        var previous = Objects.Where(o => o.Abyssal is not null).ToArray();
        if (previous.SequenceEqual(contacts))
        {
            return;
        }
        Objects = Objects.Where(o => o.Abyssal is null).Concat(contacts).ToArray();
        if (!context.InBallpark)
        {
            return;
        }
        foreach (var removed in previous.ExceptBy(contacts.Select(o => o.Id), o => o.Id))
        {
            update(new DestinyCommand.RemoveBall(removed.Id, false));
        }
        foreach (var changed in contacts.Where(o => previous.FirstOrDefault(p => p.Id == o.Id) is { } old && old != o))
        {
            update(new DestinyCommand.OnSlimItemChange(changed.Id, new StaticSlimItem(changed)));
        }
        var added = contacts.ExceptBy(previous.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, space.Stamp(clock))));
        }
    }

    internal void RefreshVisibleAsteroids(FlightSceneContext context)
    {
        if (!context.InBallpark)
        {
            return;
        }
        var current = Objects
            .AsValueEnumerable()
            .Where(o =>
                IsPublicAsteroid(o)
                && context.Position is { } position
                && position.DistanceTo(o.Position) <= AsteroidDefinitions.VisibilityRange
            )
            .ToArray();
        foreach (var departed in _visibleAsteroids.ExceptBy(current.Select(o => o.Id), o => o.Id))
        {
            update(new DestinyCommand.RemoveBall(departed.Id, false));
        }
        var added = current.ExceptBy(_visibleAsteroids.Select(o => o.Id), o => o.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddObjects(added, space.Stamp(clock))));
        }
        _visibleAsteroids = current;
    }

    internal void ApplyCourse(FlightSceneContext context, ShipContact ship)
    {
        void Send(DestinyCommand command)
        {
            send(new(ship.Stamp, command));
            if (ship.Pilot.ShipId == context.Pilot.ShipId)
            {
                space.RecordOrder(context.Pilot.ShipId, new(ship.Stamp, command));
            }
        }
        long id = ship.Pilot.ShipId;
        if (ship.AlignTo is { } end)
        {
            Send(new DestinyCommand.SetSpeedFraction(id, 1d));
            Send(new DestinyCommand.SetBallVelocity(id, ship.Velocity.X, ship.Velocity.Y, ship.Velocity.Z));
            Send(new DestinyCommand.WarpTo(id, end.X, end.Y, end.Z, 0d, ship.WarpFactor));
            return;
        }
        if (ship.Heading is null)
        {
            Send(new DestinyCommand.Stop(id));
        }
        else
        {
            if (ship.FollowRange is double range)
            {
                Send(new DestinyCommand.Follow(ship.FollowMode, id, ship.FollowTargetId, range));
            }
            else
            {
                Send(new DestinyCommand.GotoDirection(id, ship.Heading.X, ship.Heading.Y, ship.Heading.Z));
            }
            Send(new DestinyCommand.SetSpeedFraction(id, ship.SpeedFraction));
        }
    }

    internal void SnapshotStructure(FlightSceneContext context) => Snapshot(context, Peers(context), null);

    internal void Snapshot(FlightSceneContext context, ShipContact[] peers, ShipContact? own)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        bool entering = _observingSince is null;
        _observingSince ??= now;
        PruneGateEvents(context.Pilot.SystemId, now);
        _peers = peers;
        _peerOrderSequences.Clear();
        foreach (var peer in peers)
        {
            _peerOrderSequences[peer.Pilot.ShipId] = space.ReadOrders(peer.Pilot.ShipId).Sequence;
        }
        int stamp = space.Stamp(clock);
        var visible = VisibleObjects(context);
        Npcs.Capture(context);
        _wrecks = ReadWrecks(context);
        _missiles = ReadMissiles(context);
        Drones.Capture(context);
        _visibleAsteroids = visible.Where(IsPublicAsteroid).ToArray();
        var snapshot = own is null
            ? Ballpark.StructureSnapshot(
                context.EntityId,
                context.Pilot.SystemId,
                stamp,
                visible,
                peers,
                Npcs.Contacts,
                _wrecks,
                _missiles,
                Drones.Contacts
            )
            : Ballpark.Snapshot(own, stamp, visible, peers, Npcs.Contacts, _wrecks, _missiles, Drones.Contacts) with
            {
                Boosts = commandBursts?.Incoming(own.Pilot.ShipId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            };
        send(new(stamp, new DestinyCommand.SetState(snapshot)));
        Drones.ReplayActivities();
        Npcs.Replay();
        foreach (var missile in _missiles.Where(m => !m.Flight.Orphaned))
        {
            update(new DestinyCommand.AttackIndicator(missile.SourceShipId, missile.Id, missile.TargetId, true));
        }
        if (own is not null)
        {
            effects.Replay(own, null);
        }
        foreach (var peer in peers)
        {
            effects.Replay(peer, null);
            if (!entering)
            {
                ObserveGateArrival(peer, now);
                ObserveGateDeparture(peer, now);
            }
        }
        PruneCloakedPeers(context.Pilot.SystemId);
        if (own is not null && (own.Warp is not null || own.AlignTo is not null))
        {
            effects.WarpEffect(context.Pilot.ShipId, true);
        }
    }

    internal void CorrectPosition(long id, SpacePosition position, SpacePosition velocity, int stamp) =>
        FlightEffects.CorrectPosition(send, id, position, velocity, stamp);

    private ShipContact[] Peers(FlightSceneContext context) => space.Peers(context.Pilot.SystemId, context.EntityId, context.Position);

    internal void RefreshPeers(FlightSceneContext context)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        PruneGateEvents(context.Pilot.SystemId, now);
        foreach (var arrival in space.GateArrivals(context.Pilot.SystemId, context.EntityId, context.Position))
        {
            ObserveGateArrival(arrival, now);
        }
        var current = Peers(context);
        var previousById = _peers.ToDictionary(ship => ship.Pilot.ShipId);
        foreach (var departed in _peers.ExceptBy(current.Select(ship => ship.Pilot.ShipId), ship => ship.Pilot.ShipId))
        {
            if (space.IsCloaked(departed.Pilot.ShipId))
            {
                _cloakedPeers.Add(departed.Pilot.ShipId);
                update(new DestinyCommand.CloakBall(departed.Pilot.ShipId, space.IsGateCloaked(departed.Pilot.ShipId)));
                update(
                    new DestinyCommand.SpecialEffect(departed.Pilot.ShipId, null, null, null, null, "effects.Cloak", false, true, false)
                );
            }
            update(new DestinyCommand.RemoveBall(departed.Pilot.ShipId, health?.WasDestroyed(departed.Pilot.ShipId) == true));
            _peerOrderSequences.Remove(departed.Pilot.ShipId);
        }
        // Native Destiny predicts the common movement and collisions between stamped course changes.
        // Absolute position setters collapse its interpolation interval and are reserved for recovery.
        foreach (var ship in current)
        {
            var previous = previousById.GetValueOrDefault(ship.Pilot.ShipId);
            if (previous is null)
            {
                // AddBalls takes a single chunk. Replacing the entire park resets the observer's origin.
                update(new DestinyCommand.AddBalls(Ballpark.AddShip(ship.ForObserver(), space.Stamp(clock))));
                if (_cloakedPeers.Remove(ship.Pilot.ShipId))
                {
                    update(new DestinyCommand.UncloakBall(ship.Pilot.ShipId));
                    update(
                        new DestinyCommand.SpecialEffect(ship.Pilot.ShipId, null, null, null, null, "effects.Uncloak", false, true, false)
                    );
                }
                _peerOrderSequences[ship.Pilot.ShipId] = space.ReadOrders(ship.Pilot.ShipId).Sequence;
                effects.Replay(ship, null);
                ObserveGateArrival(ship, now);
                ObserveGateDeparture(ship, now);
                if (ship.Arriving)
                {
                    effects.ArrivalEffect(ship.Pilot.ShipId);
                }
                // The client plays the arrival flash when it loads a ship already in warp.
                continue;
            }
            ObserveGateArrival(ship, now);
            ObserveGateDeparture(ship, now);
            effects.Replay(ship, previous);
            if (
                ship.DirtTime != previous.DirtTime
                || ship.Pilot.CorpId != previous.Pilot.CorpId
                || ship.Pilot.AllianceId != previous.Pilot.AllianceId
                || ship.Pilot.WarFaction != previous.Pilot.WarFaction
                || !(ship.Compression ?? []).SequenceEqual(previous.Compression ?? [])
            )
            {
                update(new DestinyCommand.OnSlimItemChange(ship.Pilot.ShipId, new ShipSlimItem(ship)));
            }
            if (
                ship.Health is { } currentHealth
                && previous.Health is { } previousHealth
                && (
                    currentHealth.Remaining.Armor != previousHealth.Remaining.Armor
                    || currentHealth.Remaining.Structure != previousHealth.Remaining.Structure
                    || currentHealth.Remaining.Shield != previousHealth.Remaining.Shield
                )
            )
            {
                NotifyHealth(currentHealth);
            }
            long after = _peerOrderSequences.GetValueOrDefault(ship.Pilot.ShipId);
            var orders = space.ReadOrders(ship.Pilot.ShipId);
            long latest = orders.Sequence;
            bool missing = orders.MissingOrders(after) || ship.Stamp - previous.Stamp > SharedSpace.MovementHistoryTicks;
            bool changedWarp = ship.Warp != previous.Warp;
            bool refreshCourse = missing || orders.Restarted(after) || changedWarp || ship.AlignTo != previous.AlignTo;
            if (refreshCourse && ship.Warp is not null)
            {
                // Native AddBalls resynchronizes the existing ball with the authoritative warp stamp.
                update(new DestinyCommand.AddBalls(Ballpark.AddShip(ship, space.Stamp(clock))));
            }
            else if (refreshCourse)
            {
                CorrectPosition(ship.Pilot.ShipId, ship.Pilot.Position!, ship.Velocity, ship.Stamp);
                ApplyCourse(context, ship.ForObserver());
            }
            if (!missing && after < latest)
            {
                foreach (var order in orders.Orders)
                {
                    if (order.Sequence <= after)
                    {
                        continue;
                    }
                    bool replay = order.Update.Command switch
                    {
                        DestinyCommand.WarpTo => false,
                        DestinyCommand.CommandBurstVisual visual => visual.Id == context.Pilot.ShipId
                            || current.Any(peer => peer.Pilot.ShipId == visual.Id),
                        DestinyCommand.MicroJumpVisual => true,
                        _ => !refreshCourse,
                    };
                    if (replay)
                    {
                        send(order.Update);
                    }
                }
            }
            _peerOrderSequences[ship.Pilot.ShipId] = latest;
            if (changedWarp)
            {
                effects.WarpEffect(ship.Pilot.ShipId, ship.Warp is not null);
            }
        }
        _peers = current;
        PruneCloakedPeers(context.Pilot.SystemId);
    }

    private void PruneCloakedPeers(int system)
    {
        if (_cloakedPeers.Count != 0)
        {
            // A cloak can end beyond the observer's grid. Keep its marker while
            // the hull still belongs to this system's published scene.
            _cloakedPeers.RemoveWhere(ship => !space.HasPublishedShip(system, ship));
        }
    }

    private long ExpiredGateArrival => _latestGateObservation - GateArrivalWindow;

    // Departure remains eligible at its final tick.
    private long ExpiredGateDeparture => _latestGateObservation - GateDepartureWindow - 1;

    private void PruneGateEvents(int system, long now)
    {
        // A backward wall-clock adjustment must not replay events whose windows closed.
        _latestGateObservation = Math.Max(_latestGateObservation, now);
        Prune(_gateArrivals, ExpiredGateArrival);
        Prune(_gateDepartures, ExpiredGateDeparture);

        void Prune(Dictionary<long, long> events, long expired)
        {
            foreach (var (ship, at) in events)
            {
                if (at <= expired || at > _latestGateObservation && !space.HasPublishedShip(system, ship))
                {
                    events.Remove(ship);
                }
            }
        }
    }

    private bool RememberGateEvent(Dictionary<long, long> events, long ship, long at, long expired, long now)
    {
        if (_observingSince is not long observing || at <= observing || at <= expired || events.GetValueOrDefault(ship) >= at)
        {
            return false;
        }
        events[ship] = at;
        return at <= now;
    }

    private void ObserveGateArrival(ShipContact ship, long now)
    {
        if (
            ship.GateArrival is { } arrival
            && RememberGateEvent(_gateArrivals, ship.Pilot.ShipId, arrival.OccurredAt, ExpiredGateArrival, now)
            && Objects.Any(body => body.Id == arrival.GateId && body.DestinationGateId is not null)
        )
        {
            effects.GateActivity(arrival.GateId, arriving: true);
        }
    }

    private void ObserveGateDeparture(ShipContact ship, long now)
    {
        if (
            ship.DepartureGate is long departure
            && ship.DepartureAt is long departed
            && RememberGateEvent(_gateDepartures, ship.Pilot.ShipId, departed, ExpiredGateDeparture, now)
            && Objects.FirstOrDefault(body => body.Id == departure && body.DestinationSystemId is not null) is { } gate
        )
        {
            effects.DepartureEffect(ship.Pilot.ShipId, departure, gate.DestinationSystemId!.Value, FlightTravel.JumpDurationTicks * 1000);
        }
    }
}
