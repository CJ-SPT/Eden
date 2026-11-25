using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Docking;
using Eden.Gameplay.FactionWarfare;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Combat;

namespace Eden.Protocol.Flight;

internal sealed record FittedSpaceModule(long ItemId, int TypeId, int FlagId);

internal sealed record MiningContact(
    long ModuleId,
    int TypeId,
    long Target,
    long StartedAt,
    double Duration,
    string Graphic = "effects.Laser"
);

internal sealed record WeaponContact(
    long ModuleId,
    int TypeId,
    int ChargeTypeId,
    long? Target,
    long StartedAt,
    double Duration,
    string Graphic = "effects.Laser",
    IReadOnlyList<long>? ArcTargets = null,
    DisintegratorSpool? Spool = null,
    double? FieldRange = null,
    bool Offensive = true
);

internal sealed record LocalRepairContact(
    long ModuleId,
    int TypeId,
    long StartedAt,
    double Duration,
    RepairLayer Layer = RepairLayer.Armor
);

internal sealed record SupportContact(SupportModule Module, long StartedAt, long? TargetId = null);

internal sealed record TackleContact(TackleModule Module, long TargetId, long StartedAt);

internal sealed record HardenerContact(Hardener Module, long StartedAt);

internal sealed record SignatureSuppressionContact(SignatureSuppressor Module, SignatureSuppressionSchedule Schedule);

internal sealed record AssaultDamageControlContact(AssaultDamageControl Module, AssaultDamageControlSchedule Schedule);

internal sealed record ShieldBoostContact(long ModuleId, int TypeId, long StartedAt, double Duration);

internal sealed record PropulsionContact(PropulsionModule Module, long StartedAt);

internal sealed record PilotContact(
    long PilotId,
    string Name,
    int CorpId,
    int GenderId,
    string CorporationTicker,
    long ShipId,
    int ShipTypeId,
    int ShipGroupId,
    int ShipCategoryId,
    string ShipName,
    FlightProperties Flight,
    int SystemId,
    SpacePosition? Position,
    double X,
    double Y,
    double Z,
    int? AllianceId = null,
    Eden.Gameplay.FactionWarfare.MilitiaFaction? WarFaction = null
)
{
    internal static PilotContact From(DockedPilot pilot) =>
        new(
            pilot.PilotId,
            pilot.Name,
            pilot.CorpId,
            pilot.Profile.GenderId,
            pilot.CorporationTicker,
            pilot.ShipId,
            pilot.ShipTypeId,
            pilot.ShipGroupId,
            pilot.ShipCategoryId,
            pilot.ShipName,
            pilot.Flight,
            pilot.SystemId,
            pilot.Position,
            pilot.X,
            pilot.Y,
            pilot.Z,
            pilot.Identity.AllianceId,
            pilot.Identity.WarFaction
        );
}

internal sealed record GateArrivalContact(long GateId, long OccurredAt);

internal sealed record ShipContact(
    PilotContact Pilot,
    SpacePosition Velocity,
    SpacePosition? Heading,
    double SpeedFraction,
    double? FollowRange,
    WarpCourse? Warp = null,
    SpacePosition? AlignTo = null,
    int AlignStamp = 0,
    int WarpFactor = 0,
    long FollowTargetId = 0,
    long? DepartureGate = null,
    bool Arriving = false,
    FittedSpaceModule[]? Modules = null,
    MiningContact[]? Mining = null,
    ShipHealth? Health = null,
    WeaponContact[]? Weapons = null,
    ShieldBoostContact[]? ShieldBoosts = null,
    PropulsionContact? Propulsion = null,
    NavigationMode FollowMode = NavigationMode.Follow,
    LocalRepairContact[]? LocalRepairs = null,
    HardenerContact[]? Hardeners = null,
    SupportContact[]? Support = null,
    TackleContact[]? Tackle = null,
    int Stamp = 0,
    bool Bumped = false,
    long? DirtTime = null,
    ElectronicWarfareSpaceContact[]? ElectronicWarfare = null,
    RemoteRepairCycle[]? RemoteRepairs = null,
    CapacitorCycle[]? CapacitorEffects = null,
    SignatureSuppressionContact[]? SignatureSuppressions = null,
    AssaultDamageControlContact[]? AssaultDamageControls = null,
    GateArrivalContact? GateArrival = null,
    long? DepartureAt = null,
    bool Cloaked = false,
    SpatialActivation? MicroJump = null,
    GateCamp? Camp = null,
    long GateCloakUntil = 0,
    long ConnectionId = 0,
    Eden.Gameplay.Structures.PilotTether? Tether = null,
    IReadOnlyList<ShipCompressionFacility>? Compression = null,
    SpacePosition? DestinationPoint = null
)
{
    // A remote client may only enter warp once the simulation has started the course.
    internal ShipContact ForObserver() =>
        Warp is null && AlignTo is { } destination ? this with { AlignTo = null, Heading = (destination - Pilot.Position!).Unit } : this;
}

internal sealed record MotionOrder(long Sequence, long PreviousSequence, StampedDestinyUpdate Update);

internal readonly record struct MotionOrderBatch(IReadOnlyList<MotionOrder> Orders)
{
    internal long Sequence => Orders.Count == 0 ? 0 : Orders[^1].Sequence;

    internal bool MissingOrders(long after) => Orders.Count > 0 && after < Orders[0].PreviousSequence;

    internal bool Restarted(long after) =>
        after > 0 && (Orders.Count == 0 || Orders[0] is { PreviousSequence: 0 } first && after < first.Sequence);
}

[Injectable(InjectionType.Singleton)]
internal sealed class SharedSpace(
    IPilotCrimewatch? crimewatch = null,
    TimeProvider? clock = null,
    IWorldSynchronization? synchronization = null,
    IPilotSpatialModules? spatial = null,
    IAutonomousPilotPresence? autonomous = null,
    IPilotGateCloak? gateCloaks = null,
    Eden.Gameplay.Structures.IPilotTethering? tethering = null,
    IMilitiaFlightPresence? militiaPresence = null
) : IPilotCombatPresence, IConduitArrivalEvents, Eden.Protocol.Gateway.IShipSkinVisibility
{
    // The coordinator owns copied navigation states. Connections retain their socket and notification queue.
    private readonly ConcurrentDictionary<long, ShipContact> _ships = new();

    private readonly PilotSightGrid _sightGrid = new();

    public Eden.Gameplay.Capsuleers.PilotSighting[] Observe(int system, long ship, SpacePosition origin, long sample)
    {
        var native = _sightGrid.Observe(_ships, system, ship, origin, sample, Now);
        if (tethering is not null)
        {
            native = native.Where(contact => !TetherProtected(contact.ShipId)).ToArray();
        }
        var residents = autonomous?.Observe(system, ship, origin, sample) ?? [];
        if (residents.Length == 0)
        {
            return autonomous is null ? native : native.Where(contact => !autonomous.Owns(contact.ShipId)).ToArray();
        }
        return residents
            .Where(contact => !IsCloaked(contact.ShipId) && !TetherProtected(contact.ShipId))
            .Concat(native.Where(contact => autonomous?.Owns(contact.ShipId) != true))
            .OrderBy(contact => origin.DistanceTo(contact.Position))
            .ThenBy(contact => contact.ShipId)
            .Take(12)
            .ToArray();
    }

    internal bool TetherProtected(long ship) => tethering?.Protected(ship) == true;

    private readonly OwnershipMonitor _motionOwnership = new("flight");

    internal IDisposable EnterMotion([CallerMemberName] string operation = "", int? systemId = null) =>
        synchronization?.Enter($"flight.{operation}", systemId ?? synchronization.CurrentSystem)
        ?? _motionOwnership.Enter(operation, systemId);

    internal IDisposable? UseSystem(int system) => synchronization?.UseSystem(system);

    private T RunMotion<T>(int? system, Func<T> action, string operation)
    {
        if (synchronization is not null && (system ?? synchronization.CurrentSystem) is int id)
        {
            return synchronization.Run(operation, id, action);
        }
        return action();
    }

    private void RunMotion(int? system, Action action, string operation) =>
        RunMotion(
            system,
            () =>
            {
                action();
                return true;
            },
            operation
        );

    private int? SystemFor(long ship) =>
        _ships.TryGetValue(ship, out var contact) ? contact.Pilot.SystemId
        : _motion.TryGetValue(ship, out var motion) ? motion.SystemId
        : null;

    internal const int MovementHistoryTicks = 32;

    internal SpatialStatus SpatialStatus(long ship) => spatial?.Status(ship) ?? new();

    private long Now => (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal bool IsCloaked(long ship) => SpatialStatus(ship).Cloaked(Now);

    internal bool IsGateCloaked(long ship) => SpatialStatus(ship).GateCloaked(Now);

    internal bool HasPublishedShip(int system, long ship) =>
        _ships.TryGetValue(ship, out var contact)
        && contact.Pilot.SystemId == system
        && contact.Pilot.Position is not null
        && contact.Health?.Remaining.Destroyed != true;

    private bool ArrivalProtected(ShipContact ship) => ship.Arriving && (ship.GateCloakUntil == 0 || IsGateCloaked(ship.Pilot.ShipId));

    internal void CancelGateCloak(long pilot, long ship) => gateCloaks?.Cancel(pilot, ship);

    internal bool PreservesGateCloak(long pilot, long ship, long module) => gateCloaks?.PreservesCloak(pilot, ship, module) == true;

    private readonly ConcurrentDictionary<long, MotionOrder[]> _orders = new();
    private long _lastOrderSequence;

    internal MotionOrderBatch ReadOrders(long shipId) => new(_orders.GetValueOrDefault(shipId) ?? []);

    public void RecordOrder(long shipId, StampedDestinyUpdate update)
    {
        RunMotion(SystemFor(shipId), () => RecordOrderOwned(shipId, update), nameof(RecordOrder));
    }

    private void RecordOrderOwned(long shipId, StampedDestinyUpdate update)
    {
        long? target = update.Command switch
        {
            DestinyCommand.GotoDirection command => command.Id,
            DestinyCommand.GotoPoint command => command.Id,
            DestinyCommand.Follow command => command.Id,
            DestinyCommand.Stop command => command.Id,
            DestinyCommand.SetSpeedFraction command => command.Id,
            DestinyCommand.WarpTo command => command.Id,
            DestinyCommand.SetBallPosition command => command.Id,
            DestinyCommand.SetBallVelocity command => command.Id,
            DestinyCommand.SetBallMass command => command.Id,
            DestinyCommand.SetMaxSpeed command => command.Id,
            DestinyCommand.MicroJumpVisual command => command.Id,
            DestinyCommand.CommandBurstVisual command => command.TargetId ?? command.Id,
            _ => null,
        };
        if (target != shipId)
        {
            return;
        }
        using (EnterMotion())
        {
            var batch = ReadOrders(shipId);
            // A cursor from before the same hull left must be older than any new order.
            // The previous order for this ship tells observers whether pruning lost a command.
            long sequence = Interlocked.Increment(ref _lastOrderSequence);
            _orders[shipId] = batch
                .Orders.Append(new MotionOrder(sequence, batch.Sequence, update))
                .Where(order => order.Update.Stamp >= update.Stamp - MovementHistoryTicks)
                .ToArray();
        }
    }

    private readonly ConcurrentDictionary<long, ShipMotion> _motion = new();

    internal sealed record Displacement(
        SpacePosition Position,
        NavigationState? PriorNavigation = null,
        DestinyCommand.MicroJumpVisual? Landing = null
    );

    private readonly ConcurrentDictionary<long, Displacement> _displacements = new();
    private readonly ConcurrentDictionary<long, CapitalJump> _conduitArrivals = new();

    internal bool HasConduitArrival(long ship) => _conduitArrivals.ContainsKey(ship);

    internal CapitalJump? TakeConduitArrival(long ship) => _conduitArrivals.TryRemove(ship, out var arrival) ? arrival : null;

    public void Publish(int origin, CapitalJump arrival)
    {
        long ship = arrival.Pilot.ShipId;
        RunMotion(
            origin,
            () =>
            {
                using var owned = EnterMotion(systemId: origin);
                if (SystemFor(ship) == origin)
                {
                    LeaveOwned(ship);
                    _conduitArrivals[ship] = arrival;
                }
            },
            nameof(Publish)
        );
    }

    internal Displacement? ConsumeDisplacement(long ship) => _displacements.TryRemove(ship, out var destination) ? destination : null;

    internal IReadOnlyList<MicroJumpPassenger> MicroJumpCandidates(DockedPilot source, double radius)
    {
        if (source.Position is not { } origin)
        {
            return [];
        }
        return _sightGrid
            .Nearby(source.SystemId, origin)
            .Select(id => _ships.GetValueOrDefault(id))
            .Where(ship =>
                ship is not null
                && ship.Pilot.ShipId != source.ShipId
                && ship.Pilot.SystemId == source.SystemId
                && ship.Pilot.Position is { } at
                && at.DistanceTo(origin) <= radius + source.Flight.ShipRadius + ship.Pilot.Flight.ShipRadius
                && ship.Warp is null
                && ship.DepartureGate is null
                && !ArrivalProtected(ship)
                && ship.Health?.Remaining.Destroyed != true
                && !IsCloaked(ship.Pilot.ShipId)
            )
            .Select(ship => new MicroJumpPassenger(ship!.Pilot.PilotId, ship.Pilot.ShipId, ship.Pilot.Position!))
            .ToArray();
    }

    internal void TranslateForMicroJump(int systemId, MicroJumpArrival arrival, SpatialModule source) =>
        Displace(
            systemId,
            arrival.ShipId,
            arrival.Position,
            true,
            source.Effect == DogmaEffect.MicroJumpField
                ? DestinyCommand.MicroJumpVisual.ForModule(arrival.ShipId, arrival.Position, source)
                : null
        );

    public void Displace(int systemId, long shipId, SpacePosition position) => Displace(systemId, shipId, position, false);

    private void Displace(
        int systemId,
        long shipId,
        SpacePosition position,
        bool preserveMotion,
        DestinyCommand.MicroJumpVisual? landing = null
    )
    {
        RunMotion(
            systemId,
            () =>
            {
                using var owned = EnterMotion(systemId: systemId);
                NavigationState? navigation = null;
                if (_motion.TryGetValue(shipId, out var motion) && motion.SystemId == systemId)
                {
                    if (preserveMotion)
                    {
                        navigation = motion.Navigation.Copy();
                        navigation.CancelAlignment();
                    }
                    _motion[shipId] = motion with
                    {
                        Position = position,
                        Navigation = navigation ?? new(),
                        Target = preserveMotion ? motion.Target : null,
                    };
                    _history.TryRemove(shipId, out _);
                    _displacements[shipId] = new(position, navigation, landing);
                }
                if (_ships.TryGetValue(shipId, out var contact) && contact.Pilot.SystemId == systemId)
                {
                    _sightGrid.Move(shipId, systemId, contact.Pilot.Position, systemId, position);
                    var moved = contact with
                    {
                        Pilot = contact.Pilot with { Position = position },
                        AlignTo = null,
                        Warp = null,
                        Bumped = false,
                    };
                    var displaced = preserveMotion
                        ? moved
                        : moved with
                        {
                            Velocity = SpacePosition.Zero,
                            Heading = null,
                            SpeedFraction = 0,
                            FollowRange = null,
                            FollowTargetId = 0,
                        };
                    _ships[shipId] = displaced;
                    PublishMilitiaPresence(displaced);
                    _displacements[shipId] = new(position, navigation, landing);
                }
            },
            nameof(Displace)
        );
    }

    private readonly ConcurrentDictionary<long, IReadOnlyList<ShipMotionResult>> _history = new();

    public void PublishMotion(ShipMotion ship)
    {
        RunMotion(ship.SystemId, () => PublishMotionOwned(ship), nameof(PublishMotion));
    }

    private void PublishMotionOwned(ShipMotion ship)
    {
        using (EnterMotion(systemId: ship.SystemId))
        {
            if (!_motion.TryGetValue(ship.Id, out var current) || current.Stamp <= ship.Stamp)
            {
                _motion[ship.Id] = ship with { Navigation = ship.Navigation.Copy() };
            }
        }
    }

    public IReadOnlyList<ShipMotionResult> Advance(
        ShipMotion own,
        int now,
        long wallTime,
        ISystemMotionStore store,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        return RunMotion(own.SystemId, () => AdvanceOwned(own, now, wallTime, store, objects), nameof(Advance));
    }

    private IReadOnlyList<ShipMotionResult> AdvanceOwned(
        ShipMotion own,
        int now,
        long wallTime,
        ISystemMotionStore store,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        using (EnterMotion(systemId: own.SystemId))
        {
            PublishMotion(own);
            int first = _motion.Values.Where(s => s.SystemId == own.SystemId).Min(s => s.Stamp);
            for (int stamp = first + 1; stamp <= now; stamp++)
            {
                var ships = _motion.Values.Where(s => s.SystemId == own.SystemId && s.Stamp < stamp).OrderBy(s => s.Id).ToArray();
                if (ships.Length == 0)
                {
                    continue;
                }
                // NPCs retain their one-second offline catch-up limit through MotionAt.
                var results = store.Advance(own.SystemId, stamp, wallTime, ships, objects);
                foreach (var result in results)
                {
                    var ship = result.Ship;
                    foreach (var change in result.Changes)
                    {
                        switch (change)
                        {
                            case NavigationChange.Heading heading:
                                RecordOrder(
                                    ship.Id,
                                    new(
                                        stamp - 1,
                                        new DestinyCommand.GotoDirection(
                                            ship.Id,
                                            heading.Direction.X,
                                            heading.Direction.Y,
                                            heading.Direction.Z
                                        )
                                    )
                                );
                                break;
                            case NavigationChange.Stopped:
                                RecordOrder(ship.Id, new(stamp - 1, new DestinyCommand.Stop(ship.Id)));
                                break;
                        }
                    }
                    _motion[ship.Id] = ship;
                    _history[ship.Id] = (_history.GetValueOrDefault(ship.Id) ?? [])
                        .Append(result)
                        .Where(tick => tick.Ship.Stamp >= stamp - MovementHistoryTicks)
                        .ToArray();
                    if (_ships.TryGetValue(ship.Id, out var contact))
                    {
                        _sightGrid.Move(ship.Id, contact.Pilot.SystemId, contact.Pilot.Position, ship.SystemId, ship.Position);
                        var moved = contact with
                        {
                            Pilot = contact.Pilot with { Position = ship.Position },
                            Velocity = ship.Navigation.Velocity,
                            Heading = ship.Navigation.Direction,
                            DestinationPoint = ship.Navigation.DestinationPoint,
                            SpeedFraction = ship.Navigation.SpeedFraction,
                            FollowRange = ship.Navigation.FollowTarget is null ? null : ship.Navigation.FollowRange,
                            FollowTargetId = ship.Navigation.FollowTarget?.Id ?? 0,
                            FollowMode = ship.Navigation.FollowMode,
                            Warp = ship.Navigation.Warp,
                            AlignTo = ship.Navigation.WarpDestination,
                            Stamp = stamp,
                            Bumped = result.Bumped,
                        };
                        _ships[ship.Id] = moved;
                        PublishMilitiaPresence(moved);
                    }
                }
            }
            return _history.GetValueOrDefault(own.Id)?.Where(t => t.Ship.Stamp > own.Stamp).ToArray() ?? [];
        }
    }

    private long _epoch = long.MinValue;
    private TimeProvider? _stampClock;

    // A Destiny stamp in a peer's ball record must refer to the same tick for both clients.
    public int Stamp(TimeProvider clock)
    {
        Interlocked.CompareExchange(ref _stampClock, clock, null);
        long now = clock.GetTimestamp();
        long epoch = Interlocked.CompareExchange(ref _epoch, now, long.MinValue);
        return epoch == long.MinValue ? 1 : 1 + (int)Math.Max(0, clock.GetElapsedTime(epoch, now).TotalSeconds);
    }

    public void Publish(ShipContact ship)
    {
        RunMotion(ship.Pilot.SystemId, () => PublishOwned(ship), nameof(Publish));
    }

    private void PublishOwned(ShipContact ship)
    {
        using (EnterMotion(systemId: ship.Pilot.SystemId))
        {
            if (_ships.TryGetValue(ship.Pilot.ShipId, out var current))
            {
                if (current.Stamp > ship.Stamp)
                {
                    return;
                }
                if (current.Stamp == ship.Stamp)
                {
                    ship = ship with { Bumped = current.Bumped };
                }
            }
            _ships[ship.Pilot.ShipId] = ship;
            if (current is not null && current.Pilot.SystemId != ship.Pilot.SystemId)
            {
                militiaPresence?.Withdraw(current.Pilot.SystemId, ship.Pilot.ShipId);
            }
            PublishMilitiaPresence(ship);
            _sightGrid.Move(
                ship.Pilot.ShipId,
                current?.Pilot.SystemId ?? 0,
                current?.Pilot.Position,
                ship.Pilot.SystemId,
                ship.Pilot.Position
            );
        }
    }

    public void Leave(long shipId)
    {
        RunMotion(SystemFor(shipId), () => LeaveOwned(shipId), nameof(Leave));
    }

    private void LeaveOwned(long shipId)
    {
        using (EnterMotion())
        {
            if (_ships.TryRemove(shipId, out var previous))
            {
                militiaPresence?.Withdraw(previous.Pilot.SystemId, shipId);
                _sightGrid.Move(shipId, previous.Pilot.SystemId, previous.Pilot.Position, 0, null);
            }
            _motion.TryRemove(shipId, out _);
            _displacements.TryRemove(shipId, out _);
            _conduitArrivals.TryRemove(shipId, out _);
            _history.TryRemove(shipId, out _);
            _orders.TryRemove(shipId, out _);
        }
    }

    internal void ApplyDamage(ShipHealth health)
    {
        RunMotion(SystemFor(health.ShipId), () => ApplyDamageOwned(health), nameof(ApplyDamage));
    }

    private void ApplyDamageOwned(ShipHealth health)
    {
        using (EnterMotion())
        {
            if (health.Remaining.Destroyed)
            {
                Leave(health.ShipId);
            }
            else if (_ships.TryGetValue(health.ShipId, out var ship))
            {
                _ships[health.ShipId] = ship with { Health = health };
            }
        }
    }

    public NpcTargetCandidate[] NpcTargets(int systemId) =>
        TargetableShips(systemId)
            .Where(ship => !TetherProtected(ship.Pilot.ShipId))
            .Select(ship => new NpcTargetCandidate(ship.Pilot.PilotId, ship.Pilot.ShipId))
            .ToArray();

    private void PublishMilitiaPresence(ShipContact ship)
    {
        if (militiaPresence is null || ship.ConnectionId == 0)
        {
            return;
        }
        var pilot = ship.Pilot;
        if (
            pilot.WarFaction.HasValue
            && pilot.Position is { } position
            && InTargetableFlight(ship)
            && !ship.Cloaked
            && !(ship.Arriving && (ship.GateCloakUntil == 0 || ship.GateCloakUntil > Now))
        )
        {
            militiaPresence.Publish(pilot.SystemId, new(pilot.PilotId, pilot.ShipId, position, ship.ConnectionId));
        }
        else
        {
            militiaPresence.Withdraw(pilot.SystemId, pilot.ShipId);
        }
    }

    private IEnumerable<ShipContact> TargetableShips(int systemId) =>
        _ships.Values.Where(ship => ship.Pilot.SystemId == systemId && Targetable(ship));

    private bool Targetable(ShipContact ship) => InTargetableFlight(ship) && !ArrivalProtected(ship) && !IsCloaked(ship.Pilot.ShipId);

    private static bool InTargetableFlight(ShipContact ship) =>
        ship.Pilot.Position is not null
        && ship.Pilot.ShipGroupId != FleetDefinitions.CapsuleGroup
        && ship.Warp is null
        && ship.AlignTo is null
        && ship.DepartureGate is null
        && ship.Health?.Remaining.Destroyed != true;

    IReadOnlySet<long> Eden.Protocol.Gateway.IShipSkinVisibility.VisibleShipIds(ShipSkinViewer viewer)
    {
        if (viewer.Position is null || viewer.SystemId <= 0)
        {
            return ImmutableHashSet<long>.Empty;
        }

        return RunMotion<IReadOnlySet<long>>(
            viewer.SystemId,
            () =>
            {
                if (
                    !_ships.TryGetValue(viewer.ShipId, out var contact)
                    || contact.Pilot.PilotId != viewer.PilotId
                    || contact.Pilot.SystemId != viewer.SystemId
                    || contact.Pilot.Position is not { } position
                )
                {
                    return ImmutableHashSet<long>.Empty;
                }

                var ids = PeersOwned(viewer.SystemId, viewer.ShipId, position).Select(peer => peer.Pilot.ShipId).ToHashSet();
                ids.Add(viewer.ShipId);
                return ids;
            },
            nameof(Eden.Protocol.Gateway.IShipSkinVisibility.VisibleShipIds)
        );
    }

    public ShipContact[] Peers(DockedPilot pilot) => Peers(pilot.SystemId, pilot.ShipId, pilot.Position);

    internal ShipContact[] Peers(int system, long viewerId, SpacePosition? viewer) =>
        viewer is null ? [] : RunMotion(system, () => PeersOwned(system, viewerId, viewer), nameof(Peers));

    internal ShipContact? FindTargetablePeer(int system, long viewer, SpacePosition? origin, long id)
    {
        return origin is null || id == viewer
            ? null
            : RunMotion(system, () => FindTargetablePeerOwned(system, origin, id), nameof(FindTargetablePeer));
    }

    private ShipContact? FindTargetablePeerOwned(int system, SpacePosition origin, long id)
    {
        using var ownership = EnterMotion(systemId: system);
        var ship = _ships.GetValueOrDefault(id);
        if (ship is null && crimewatch?.LoggedOff(system).FirstOrDefault(p => p.Pilot.ShipId == id) is { } offline)
        {
            ship = new(PilotContact.From(offline.Pilot), SpacePosition.Zero, null, 0, null);
        }
        if (
            ship?.Pilot.Position is not { } position
            || ship.Pilot.SystemId != system
            || ship.Warp is not null
            || ship.DepartureGate is not null
            || ArrivalProtected(ship)
            || ship.Health?.Remaining.Destroyed == true
            || IsCloaked(id)
            || origin.DistanceTo(position) > NavigationDefinitions.MaximumTargetDistance
        )
        {
            return null;
        }
        if (autonomous?.Owns(id) != true)
        {
            return ship;
        }
        // The published ball can lag a committed bot flight step.
        return
            autonomous.Read(system, id) is { Protected: false, Sighting: var current }
            && origin.DistanceTo(current.Position) <= NavigationDefinitions.MaximumTargetDistance
            ? ship with
            {
                Pilot = ship.Pilot with { Position = current.Position },
                Velocity = current.Velocity,
            }
            : null;
    }

    internal IEnumerable<ShipContact> GateArrivals(DockedPilot pilot) => GateArrivals(pilot.SystemId, pilot.ShipId, pilot.Position);

    internal IEnumerable<ShipContact> GateArrivals(int systemId, long observerId, SpacePosition? viewer) =>
        viewer is null
            ? []
            : _ships.Values.Where(ship =>
                ship.GateArrival is not null
                && ship.Pilot.SystemId == systemId
                && IsGateCloaked(ship.Pilot.ShipId)
                && ship.Pilot.ShipId != observerId
                && ship.Pilot.Position is { } position
                && viewer.DistanceTo(position) <= NavigationDefinitions.MaximumTargetDistance
            );

    internal CommandBurstCandidate[] CommandBurstCandidates(DockedPilot pilot) =>
        RunMotion(
            pilot.SystemId,
            () =>
            {
                using var motion = EnterMotion(systemId: pilot.SystemId);
                return _ships
                    .Values.Where(ship =>
                        ship.Pilot.SystemId == pilot.SystemId
                        && ship.Pilot.ShipId != pilot.ShipId
                        && ship.Pilot.Position is not null
                        && ship.Pilot.ShipGroupId != FleetDefinitions.CapsuleGroup
                        && ship.Warp is null
                        && ship.DepartureGate is null
                        && !ArrivalProtected(ship)
                        && ship.Health?.Remaining.Destroyed != true
                        && !IsCloaked(ship.Pilot.ShipId)
                    )
                    .Select(ship => new CommandBurstCandidate(ship.Pilot.PilotId, ship.Pilot.ShipId, ship.Pilot.Position!))
                    .ToArray();
            },
            nameof(CommandBurstCandidates)
        );

    internal SmartbombShip[] SmartbombShips(DockedPilot pilot, bool includeCloaked = true)
    {
        if (pilot.Position is not { } origin)
        {
            return [];
        }
        return RunMotion(
            pilot.SystemId,
            () =>
            {
                using var motion = EnterMotion(systemId: pilot.SystemId);
                var online = _ships
                    .Values.Where(ship =>
                        ship.Pilot.SystemId == pilot.SystemId
                        && ship.Pilot.ShipId != pilot.ShipId
                        && ship.Pilot.Position is { } position
                        && Near(position, ship.Pilot.Flight.ShipRadius)
                        && ship.Warp is null
                        && ship.DepartureGate is null
                        && !ArrivalProtected(ship)
                        && !IsGateCloaked(ship.Pilot.ShipId)
                        && (includeCloaked || SpatialStatus(ship.Pilot.ShipId).CloakModuleId == 0)
                        && ship.Health?.Remaining.Destroyed != true
                    )
                    .Select(ship => new SmartbombShip(ship.Pilot.ShipId, ship.Pilot.Position!, ship.Velocity));
                var offline =
                    crimewatch
                        ?.LoggedOff(pilot.SystemId)
                        .Where(entry =>
                            entry.Pilot.Position is { } position
                            && Near(position, entry.Pilot.Flight.ShipRadius)
                            && !_ships.ContainsKey(entry.Pilot.ShipId)
                            && !IsGateCloaked(entry.Pilot.ShipId)
                        )
                        .Select(entry => new SmartbombShip(entry.Pilot.ShipId, entry.Pilot.Position!))
                    ?? [];
                return online.Concat(offline).ToArray();
            },
            nameof(SmartbombShips)
        );

        bool Near(SpacePosition position, double radius) =>
            SmartbombRules.InRange(origin, pilot.Flight.ShipRadius, position, radius, NavigationDefinitions.MaximumTargetDistance);
    }

    private ShipContact[] PeersOwned(int system, long viewerId, SpacePosition viewer)
    {
        using (EnterMotion(systemId: system))
        {
            var offline =
                crimewatch?.LoggedOff(system).Select(p => new ShipContact(PilotContact.From(p.Pilot), SpacePosition.Zero, null, 0, null))
                ?? [];
            return _ships
                .Values.Concat(offline)
                .GroupBy(s => s.Pilot.ShipId)
                .Select(g => g.First())
                .Where(ship =>
                    ship.Pilot.SystemId == system
                    && ship.Pilot.ShipId != viewerId
                    && !IsCloaked(ship.Pilot.ShipId)
                    && PeerInRange(ship, viewer)
                )
                .OrderBy(ship => ship.Pilot.ShipId)
                .Select(ExpireTimedDefenses)
                .ToArray();
        }
    }

    private bool PeerInRange(ShipContact ship, SpacePosition viewer)
    {
        if (ship.Pilot.Position is not { } position || viewer.DistanceTo(position) > NavigationDefinitions.MaximumTargetDistance)
        {
            return false;
        }
        var visibilityClock = clock ?? Volatile.Read(ref _stampClock);
        if (ship.Warp is not { } warp || visibilityClock is null)
        {
            return true;
        }
        // Destiny keeps advancing a published warp between observations. A stale
        // source position must not retain its now-distant ball in the overview.
        var rendered = warp.Sample(Stamp(visibilityClock), exitSpeed: 0).Position;
        return viewer.DistanceTo(rendered) <= NavigationDefinitions.MaximumTargetDistance;
    }

    private ShipContact ExpireTimedDefenses(ShipContact ship)
    {
        if (ship.GateCloakUntil != 0 && !IsGateCloaked(ship.Pilot.ShipId))
        {
            ship = ship with { Cloaked = SpatialStatus(ship.Pilot.ShipId).CloakModuleId != 0, GateCloakUntil = 0, Arriving = false };
        }
        if (ship.SignatureSuppressions is not { Length: > 0 } && ship.AssaultDamageControls is not { Length: > 0 })
        {
            return ship;
        }
        long now = (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime.ToFileTimeUtc();
        bool signatureExpired = ship.SignatureSuppressions?.Any(e => !e.Schedule.Active(now)) ?? false;
        bool assaultExpired = ship.AssaultDamageControls?.Any(e => !e.Schedule.Active(now)) ?? false;
        if (!signatureExpired && !assaultExpired)
        {
            return ship;
        }
        var current = ship with
        {
            SignatureSuppressions = signatureExpired
                ? ship.SignatureSuppressions!.Where(e => e.Schedule.Active(now)).ToArray()
                : ship.SignatureSuppressions,
            AssaultDamageControls = assaultExpired
                ? ship.AssaultDamageControls!.Where(e => e.Schedule.Active(now)).ToArray()
                : ship.AssaultDamageControls,
        };
        _ships.TryUpdate(ship.Pilot.ShipId, current, ship);
        return current;
    }

    public PilotCombatPresence? FindOnline(int systemId, long shipId)
    {
        return RunMotion(systemId, () => FindOnlineOwned(systemId, shipId), nameof(FindOnline));
    }

    private PilotCombatPresence? FindOnlineOwned(int systemId, long shipId)
    {
        using (EnterMotion(systemId: systemId))
        {
            if (autonomous?.Owns(shipId) == true)
            {
                return autonomous.Read(systemId, shipId) is { Protected: false } resident && !IsCloaked(shipId)
                    ? new(shipId, resident.Sighting.Position, resident.Sighting.Velocity)
                    : null;
            }
            if (!_ships.TryGetValue(shipId, out var ship))
            {
                return null;
            }
            return
                ship.Pilot.SystemId == systemId
                && ship.Pilot.Position is { } position
                && ship.Warp is null
                && ship.DepartureGate is null
                && !ArrivalProtected(ship)
                && !IsCloaked(ship.Pilot.ShipId)
                && ship.Health?.Remaining.Destroyed != true
                && !TetherProtected(shipId)
                ? new(shipId, position, ship.Velocity)
                : null;
        }
    }

    public PilotCombatPresence? Find(int systemId, long shipId)
    {
        return RunMotion(systemId, () => FindOwned(systemId, shipId), nameof(Find));
    }

    public bool IsAreaProtected(int systemId, long shipId) =>
        RunMotion(
            systemId,
            () =>
                IsGateCloaked(shipId)
                || TetherProtected(shipId)
                || (
                    autonomous?.Owns(shipId) == true
                        ? autonomous.Read(systemId, shipId)?.Protected == true
                        : _ships.TryGetValue(shipId, out var ship)
                            && (ship.Warp is not null || ship.DepartureGate is not null || ArrivalProtected(ship))
                ),
            nameof(IsAreaProtected)
        );

    public bool IsTravelProtected(int systemId, long shipId) =>
        RunMotion(
            systemId,
            () => _ships.TryGetValue(shipId, out var ship) && (ship.Warp is not null || ship.DepartureGate is not null || ship.Arriving),
            nameof(IsTravelProtected)
        );

    public PilotCombatPresence? FindArea(int systemId, long shipId) =>
        RunMotion(
            systemId,
            () =>
                autonomous?.Owns(shipId) == true
                    ? autonomous.Read(systemId, shipId) is { } resident
                        ? new PilotCombatPresence(shipId, resident.Sighting.Position, resident.Sighting.Velocity)
                        : null
                    : _ships.TryGetValue(shipId, out var ship) && ship.Pilot.SystemId == systemId && ship.Pilot.Position is { } position
                        ? new PilotCombatPresence(shipId, position, ship.Velocity)
                        : null,
            nameof(FindArea)
        );

    public IReadOnlyList<long> NearbyShips(int systemId, SpacePosition origin) => _sightGrid.Nearby(systemId, origin);

    private PilotCombatPresence? FindOwned(int systemId, long shipId)
    {
        using (EnterMotion(systemId: systemId))
        {
            if (IsGateCloaked(shipId))
            {
                return null;
            }
            if (_ships.ContainsKey(shipId) || autonomous?.Owns(shipId) == true)
            {
                return FindOnline(systemId, shipId);
            }
            return crimewatch?.LoggedOff(systemId).FirstOrDefault(p => p.Pilot.ShipId == shipId)?.Pilot.Position is { } offline
                ? new(shipId, offline, SpacePosition.Zero)
                : null;
        }
    }
}
