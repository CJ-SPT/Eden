using System.Collections.Concurrent;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Propulsion;
using Eden.Gameplay.Repair;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Propulsion;

namespace Eden.Protocol.Flight;

internal sealed record FittedSpaceModule(long ItemId, int TypeId, int FlagId);

internal sealed record MiningContact(long ModuleId, int TypeId, long Target, long StartedAt, double Duration);

internal sealed record WeaponContact(
    long ModuleId,
    int TypeId,
    int ChargeTypeId,
    long Target,
    long StartedAt,
    double Duration,
    string Graphic = "effects.Laser"
);

internal sealed record LocalRepairContact(
    long ModuleId,
    int TypeId,
    long StartedAt,
    double Duration,
    RepairLayer Layer = RepairLayer.Armor
);

internal sealed record SupportContact(SupportModule Module, long StartedAt);

internal sealed record TackleContact(TackleModule Module, long TargetId, long StartedAt);

internal sealed record HardenerContact(Hardener Module, long StartedAt);

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
    double Z
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
            pilot.Z
        );
}

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
    bool Bumped = false
);

internal sealed record MotionOrder(long Sequence, StampedDestinyUpdate Update);

[Injectable(InjectionType.Singleton)]
internal sealed class SharedSpace
{
    // The coordinator owns copied navigation states. Connections retain their socket and notification queue.
    private readonly ConcurrentDictionary<long, ShipContact> _ships = new();

    internal object MotionLock { get; } = new();
    internal const int MovementHistoryTicks = 32;
    private readonly Dictionary<long, List<MotionOrder>> _orders = new();
    private readonly Dictionary<long, long> _orderSequences = new();

    public long OrderSequence(long shipId) => _orderSequences.GetValueOrDefault(shipId);

    public IReadOnlyList<MotionOrder> Orders(long shipId, long after) =>
        _orders.GetValueOrDefault(shipId)?.Where(o => o.Sequence > after).ToArray() ?? [];

    public bool MissingOrders(long shipId, long after) =>
        _orders.TryGetValue(shipId, out var orders) && orders.Count > 0 && after < orders[0].Sequence - 1;

    public void RecordOrder(long shipId, StampedDestinyUpdate update)
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
            _ => null,
        };
        if (target != shipId)
        {
            return;
        }
        lock (MotionLock)
        {
            long sequence = _orderSequences.GetValueOrDefault(shipId) + 1;
            _orderSequences[shipId] = sequence;
            if (!_orders.TryGetValue(shipId, out var orders))
            {
                orders = [];
                _orders.Add(shipId, orders);
            }
            orders.Add(new(sequence, update));
            orders.RemoveAll(o => o.Update.Stamp < update.Stamp - MovementHistoryTicks);
        }
    }

    private readonly Dictionary<long, ShipMotion> _motion = new();
    private readonly Dictionary<long, List<ShipMotionResult>> _history = new();

    public void PublishMotion(ShipMotion ship)
    {
        lock (MotionLock)
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
        lock (MotionLock)
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
                            case NavigationChange.Collision collision:
                                RecordOrder(
                                    ship.Id,
                                    new(
                                        stamp,
                                        new DestinyCommand.SetBallPosition(
                                            ship.Id,
                                            collision.Position.X,
                                            collision.Position.Y,
                                            collision.Position.Z
                                        )
                                    )
                                );
                                RecordOrder(ship.Id, new(stamp, new DestinyCommand.Stop(ship.Id)));
                                RecordOrder(ship.Id, new(stamp, new DestinyCommand.SetBallVelocity(ship.Id, 0, 0, 0)));
                                break;
                        }
                    }
                    _motion[ship.Id] = ship;
                    if (!_history.TryGetValue(ship.Id, out var history))
                    {
                        history = [];
                        _history.Add(ship.Id, history);
                    }
                    history.Add(result);
                    history.RemoveAll(t => t.Ship.Stamp < stamp - MovementHistoryTicks);
                    if (_ships.TryGetValue(ship.Id, out var contact))
                    {
                        _ships[ship.Id] = contact with
                        {
                            Pilot = contact.Pilot with { Position = ship.Position },
                            Velocity = ship.Navigation.Velocity,
                            Heading = ship.Navigation.Direction,
                            SpeedFraction = ship.Navigation.SpeedFraction,
                            FollowRange = ship.Navigation.FollowTarget is null ? null : ship.Navigation.FollowRange,
                            FollowTargetId = ship.Navigation.FollowTarget?.Id ?? 0,
                            FollowMode = ship.Navigation.FollowMode,
                            Warp = ship.Navigation.Warp,
                            AlignTo = ship.Navigation.WarpDestination,
                            Stamp = stamp,
                            Bumped = result.Bumped,
                        };
                    }
                }
            }
            return _history.GetValueOrDefault(own.Id)?.Where(t => t.Ship.Stamp > own.Stamp).ToArray() ?? [];
        }
    }

    private long _epoch = long.MinValue;

    // A Destiny stamp in a peer's ball record must refer to the same tick for both clients.
    public int Stamp(TimeProvider clock)
    {
        long now = clock.GetTimestamp();
        long epoch = Interlocked.CompareExchange(ref _epoch, now, long.MinValue);
        return epoch == long.MinValue ? 1 : 1 + (int)Math.Max(0, clock.GetElapsedTime(epoch, now).TotalSeconds);
    }

    public void Publish(ShipContact ship)
    {
        lock (MotionLock)
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
        }
    }

    public void Leave(long shipId)
    {
        lock (MotionLock)
        {
            _ships.TryRemove(shipId, out _);
            _motion.Remove(shipId);
            _history.Remove(shipId);
            _orders.Remove(shipId);
        }
    }

    public NpcTargetCandidate[] NpcTargets(int systemId) =>
        _ships
            .Values.Where(ship =>
                ship.Pilot.SystemId == systemId
                && ship.Pilot.Position is not null
                && ship.Pilot.ShipGroupId != FleetDefinitions.CapsuleGroup
                && ship.Warp is null
                && ship.AlignTo is null
                && ship.DepartureGate is null
                && !ship.Arriving
                && ship.Health?.Remaining.Destroyed != true
            )
            .Select(ship => new NpcTargetCandidate(ship.Pilot.PilotId, ship.Pilot.ShipId))
            .ToArray();

    public ShipContact[] Peers(DockedPilot pilot)
    {
        lock (MotionLock)
        {
            return _ships
                .Values.Where(ship =>
                    ship.Pilot.SystemId == pilot.SystemId
                    && ship.Pilot.ShipId != pilot.ShipId
                    && pilot.Position is { } viewer
                    && ship.Pilot.Position is { } other
                    && viewer.DistanceTo(other) <= NavigationDefinitions.MaximumTargetDistance
                )
                .OrderBy(ship => ship.Pilot.ShipId)
                .ToArray();
        }
    }
}
