using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.Gameplay.Mining;
using Eden.Protocol.Targeting;
using ZLinq;

namespace Eden.Protocol.Flight;

internal readonly record struct FlightSceneContext(DockedPilot Pilot, bool InBallpark, bool CanTarget, int SimulatedStamp);

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
    IPilotDrones? drones
)
{
    private FlightNpcScene? _npcScene;
    private FlightDroneScene? _droneScene;
    private ShipContact[] _peers = [];
    private readonly HashSet<long> _cloakedPeers = [];
    private readonly Dictionary<long, long> _peerOrderSequences = new();
    private readonly Dictionary<long, long> _gateArrivals = new();
    private readonly Dictionary<long, long> _gateDepartures = new();
    private long? _observingSince;
    private StaticSpaceObject[] _visibleAsteroids = [];
    private IReadOnlyList<StaticSpaceObject> _anomalies = [];
    private IReadOnlyList<StaticSpaceObject> _structures = [];
    private WreckContact[] _wrecks = [];
    private MissileContact[] _missiles = [];

    private FlightNpcScene Npcs => _npcScene ??= new(space, clock, encounters, effects, update, send);
    private FlightDroneScene Drones => _droneScene ??= new(space, clock, drones, update);

    internal IReadOnlyList<StaticSpaceObject> Objects { get; private set; } = objects;
    internal IReadOnlyList<NpcContact> VisibleNpcs => Npcs.Contacts;
    internal IReadOnlyList<WreckContact> VisibleWrecks => _wrecks;
    internal IReadOnlyList<MissileContact> VisibleMissiles => _missiles;
    internal IReadOnlyList<DroneContact> VisibleDrones => Drones.Contacts;

    internal void RefreshNpcs(FlightSceneContext context) => Npcs.RefreshNpcs(context);

    internal void RefreshDrones(FlightSceneContext context) => Drones.RefreshDrones(context);

    internal void NotifyNpcWeapon(long npcId, NpcWeaponContact weapon) => Npcs.NotifyNpcWeapon(npcId, weapon);

    internal void NotifyHealth(ShipHealth current) => Npcs.NotifyHealth(current);

    internal void StopNpcWeapons(long destroyed) => Npcs.StopNpcWeapons(destroyed);

    internal bool NpcInWarp(long id) => Npcs.InWarp(id);

    internal void ReplaceObjects(IReadOnlyList<StaticSpaceObject> destination) => Objects = destination;

    internal void ForgetMissiles() => _missiles = [];

    internal void LeaveSpace()
    {
        Objects = Objects.ExceptBy(_anomalies.Select(o => o.Id), o => o.Id).ToArray();
        _anomalies = [];
        _peers = [];
        _peerOrderSequences.Clear();
        _cloakedPeers.Clear();
        _gateArrivals.Clear();
        _gateDepartures.Clear();
        _observingSince = null;
        _visibleAsteroids = [];
        _missiles = [];
        Npcs.Clear();
        _wrecks = [];
    }

    internal TargetContact? FindTarget(FlightSceneContext context, long id)
    {
        if (!context.CanTarget)
        {
            return null;
        }
        if (space.Peers(context.Pilot).FirstOrDefault(s => s.Pilot.ShipId == id && s.Warp is null && s.DepartureGate is null) is { } ship)
        {
            return new(id, ship.Pilot.ShipTypeId, ship.Pilot.Position!, ship.Pilot.Flight.ShipRadius);
        }
        if (Npcs.Contacts.FirstOrDefault(n => n.Id == id && n.Warp is null) is { } npc)
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
                    && (MiningDefinitions.IsResource(o.CategoryId, o.GroupId) || Eden.Gameplay.Exploration.HackingRules.IsContainer(o.Id))
                )
                is { } targetObject
            ? new(id, targetObject.TypeId, targetObject.Position, targetObject.Radius)
            : null;
    }

    internal PilotWeaponTarget? FindPilotWeaponTarget(FlightSceneContext context, long id) =>
        context.CanTarget
        && space.Peers(context.Pilot).FirstOrDefault(s => s.Pilot.ShipId == id && s.Warp is null && s.DepartureGate is null && !s.Arriving)
            is { } ship
            ? new(id, ship.Velocity)
            : null;

    private StaticSpaceObject[] VisibleObjects(FlightSceneContext context) =>
        Objects
            .AsValueEnumerable()
            .Where(o =>
                !MiningDefinitions.IsResource(o.CategoryId, o.GroupId)
                || (context.Pilot.Position is { } position && position.DistanceTo(o.Position) <= AsteroidDefinitions.VisibilityRange)
            )
            .ToArray();

    private MissileContact[] ReadMissiles(FlightSceneContext context) =>
        context.Pilot.Position is not { } position
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
            update(new DestinyCommand.RemoveBall(departed.Id, missiles?.Impacted(departed.Id) == true));
        }
        var added = current.ExceptBy(_missiles.Select(m => m.Id), m => m.Id).ToArray();
        if (added.Length > 0)
        {
            update(new DestinyCommand.AddBalls(Ballpark.AddMissiles(added, space.Stamp(clock))));
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
                var point = missile.Flight.Goto;
                update(new DestinyCommand.GotoPoint(missile.Id, point.X, point.Y, point.Z));
            }
        }
        _missiles = current;
    }

    internal StaticSpaceObject? NavigationTarget(FlightSceneContext context, long id)
    {
        if (id == context.Pilot.ShipId || context.Pilot.Position is not { } position)
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
                        WreckDefinitions.Category,
                        checked((int)wreck.OwnerId),
                        wreck.Position,
                        wreck.Radius
                    )
                : null
            );
        return target is not null && position.DistanceTo(target.Position) <= NavigationDefinitions.MaximumTargetDistance ? target : null;
    }

    private WreckContact[] ReadWrecks(FlightSceneContext context) =>
        context.Pilot.Position is not { } position
            ? []
            : (wrecks?.List(context.Pilot.SystemId, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) ?? [])
                .Where(w =>
                    w.SystemId == context.Pilot.SystemId
                    && (w.PlanetId.HasValue || position.DistanceTo(w.Position) <= WreckDefinitions.VisibilityRange)
                )
                .ToArray();

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
        Objects = Objects.Where(o => !MiningDefinitions.IsResource(o.CategoryId, o.GroupId)).Concat(asteroids).ToArray();
        RefreshVisibleAsteroids(context);
    }

    internal void RefreshStructures(FlightSceneContext context, IReadOnlyList<StaticSpaceObject> structures)
    {
        if (ReferenceEquals(_structures, structures) || _structures.SequenceEqual(structures))
        {
            return;
        }
        var previous = Objects.Where(o => o.CategoryId == Eden.Gameplay.Structures.StructureRules.CategoryId).ToArray();
        Objects = Objects.Where(o => o.CategoryId != Eden.Gameplay.Structures.StructureRules.CategoryId).Concat(structures).ToArray();
        _structures = structures;
        if (!context.InBallpark)
        {
            return;
        }
        foreach (var removed in previous.ExceptBy(structures.Select(o => o.Id), o => o.Id))
        {
            update(new DestinyCommand.RemoveBall(removed.Id, false));
        }
        foreach (var changed in structures.Where(o => previous.FirstOrDefault(p => p.Id == o.Id) is { } old && old != o))
        {
            update(new DestinyCommand.OnSlimItemChange(changed.Id, new StaticSlimItem(changed)));
        }
        var added = structures.ExceptBy(previous.Select(o => o.Id), o => o.Id).ToArray();
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
        var current = VisibleObjects(context).Where(o => MiningDefinitions.IsResource(o.CategoryId, o.GroupId)).ToArray();
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

    internal void Snapshot(FlightSceneContext context, ShipContact[] peers, ShipContact own)
    {
        bool entering = _observingSince is null;
        _observingSince ??= clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        _peers = peers;
        _peerOrderSequences.Clear();
        foreach (var peer in peers)
        {
            _peerOrderSequences[peer.Pilot.ShipId] = space.OrderSequence(peer.Pilot.ShipId);
        }
        int stamp = space.Stamp(clock);
        var visible = VisibleObjects(context);
        Npcs.Capture(context);
        _wrecks = ReadWrecks(context);
        _missiles = ReadMissiles(context);
        Drones.Capture(context);
        _visibleAsteroids = visible.Where(o => MiningDefinitions.IsResource(o.CategoryId, o.GroupId)).ToArray();
        send(
            new(
                stamp,
                new DestinyCommand.SetState(
                    Ballpark.Snapshot(own, stamp, visible, peers, Npcs.Contacts, _wrecks, _missiles, Drones.Contacts)
                )
            )
        );
        Drones.ReplayActivities();
        Npcs.Replay();
        effects.Replay(own, null);
        foreach (var peer in peers)
        {
            effects.Replay(peer, null);
            if (!entering)
            {
                ObserveGateArrival(peer);
                ObserveGateDeparture(peer);
            }
        }
        if (own.Warp is not null || own.AlignTo is not null)
        {
            effects.WarpEffect(context.Pilot.ShipId, true);
        }
    }

    internal void CorrectPosition(long id, SpacePosition position, SpacePosition velocity, int stamp) =>
        FlightEffects.CorrectPosition(send, id, position, velocity, stamp);

    internal void RefreshPeers(FlightSceneContext context, ShipContact contact)
    {
        var current = space.Peers(context.Pilot);
        var previousById = _peers.ToDictionary(ship => ship.Pilot.ShipId);
        if (
            current.Any(ship =>
                ship.Warp is not null
                && previousById.TryGetValue(ship.Pilot.ShipId, out var old)
                && old.Warp != ship.Warp
                && old.AlignTo != ship.Warp.Destination
            )
        )
        {
            Snapshot(context, current, contact);
            return;
        }
        foreach (var departed in _peers.ExceptBy(current.Select(ship => ship.Pilot.ShipId), ship => ship.Pilot.ShipId))
        {
            if (space.SpatialStatus(departed.Pilot.ShipId).CloakModuleId != 0)
            {
                _cloakedPeers.Add(departed.Pilot.ShipId);
                update(new DestinyCommand.CloakBall(departed.Pilot.ShipId));
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
                update(new DestinyCommand.AddBalls(Ballpark.AddShip(ship, space.Stamp(clock))));
                if (_cloakedPeers.Remove(ship.Pilot.ShipId))
                {
                    update(new DestinyCommand.UncloakBall(ship.Pilot.ShipId));
                    update(
                        new DestinyCommand.SpecialEffect(ship.Pilot.ShipId, null, null, null, null, "effects.Uncloak", false, true, false)
                    );
                }
                _peerOrderSequences[ship.Pilot.ShipId] = space.OrderSequence(ship.Pilot.ShipId);
                effects.Replay(ship, null);
                ObserveGateArrival(ship);
                ObserveGateDeparture(ship);
                if (ship.Arriving)
                {
                    effects.ArrivalEffect(ship.Pilot.ShipId);
                }
                // The client plays the arrival flash when it loads a ship already in warp.
                continue;
            }
            ObserveGateArrival(ship);
            ObserveGateDeparture(ship);
            effects.Replay(ship, previous);
            if (ship.DirtTime != previous.DirtTime || ship.Pilot.CorpId != previous.Pilot.CorpId)
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
            bool missing = space.MissingOrders(ship.Pilot.ShipId, after) || ship.Stamp - previous.Stamp > SharedSpace.MovementHistoryTicks;
            if (missing)
            {
                CorrectPosition(ship.Pilot.ShipId, ship.Pilot.Position!, ship.Velocity, ship.Stamp);
                ApplyCourse(context, ship);
            }
            else
            {
                foreach (var order in space.Orders(ship.Pilot.ShipId, after))
                {
                    send(order.Update);
                }
            }
            _peerOrderSequences[ship.Pilot.ShipId] = space.OrderSequence(ship.Pilot.ShipId);
            if (ship.Warp is not null)
            {
                if (ship.Warp != previous.Warp)
                {
                    effects.WarpEffect(ship.Pilot.ShipId, true);
                }
                continue;
            }
            if (ship.AlignTo is not null)
            {
                continue;
            }
            if (previous.Warp is not null)
            {
                effects.WarpEffect(ship.Pilot.ShipId, false);
            }
        }
        _peers = current;
    }

    private void ObserveGateArrival(ShipContact ship)
    {
        if (ship.GateArrival is not { } arrival || _gateArrivals.GetValueOrDefault(ship.Pilot.ShipId) >= arrival.OccurredAt)
        {
            return;
        }
        _gateArrivals[ship.Pilot.ShipId] = arrival.OccurredAt;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (
            _observingSince is not long observing
            || arrival.OccurredAt <= observing
            || arrival.OccurredAt > now
            || now - arrival.OccurredAt >= TimeSpan.FromSeconds(FlightTravel.ArrivalDurationTicks).Ticks
            || !Objects.Any(body => body.Id == arrival.GateId && body.DestinationGateId is not null)
        )
        {
            return;
        }
        effects.GateActivity(arrival.GateId, arriving: true);
    }

    private void ObserveGateDeparture(ShipContact ship)
    {
        if (
            ship.DepartureGate is not long departure
            || ship.DepartureAt is not long departed
            || _gateDepartures.GetValueOrDefault(ship.Pilot.ShipId) >= departed
        )
        {
            return;
        }
        _gateDepartures[ship.Pilot.ShipId] = departed;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (
            _observingSince is not long observing
            || departed <= observing
            || departed > now
            || now - departed > TimeSpan.FromSeconds(FlightTravel.JumpDurationTicks).Ticks
            || Objects.FirstOrDefault(body => body.Id == departure && body.DestinationSystemId is not null) is not { } gate
        )
        {
            return;
        }
        effects.DepartureEffect(ship.Pilot.ShipId, departure, gate.DestinationSystemId!.Value, FlightTravel.JumpDurationTicks * 1000);
    }
}
