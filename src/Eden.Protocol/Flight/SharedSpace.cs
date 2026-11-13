using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Defenses;
using Eden.Gameplay.Docking;
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
    DisintegratorSpool? Spool = null
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
    SpatialActivation? MicroJump = null
);

internal sealed record MotionOrder(long Sequence, StampedDestinyUpdate Update);

[Injectable(InjectionType.Singleton)]
internal sealed class SharedSpace(
    IPilotCrimewatch? crimewatch = null,
    TimeProvider? clock = null,
    IWorldSynchronization? synchronization = null,
    IPilotSpatialModules? spatial = null
) : IPilotCombatPresence
{
    // The coordinator owns copied navigation states. Connections retain their socket and notification queue.
    private readonly ConcurrentDictionary<long, ShipContact> _ships = new();

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

    private readonly ConcurrentDictionary<long, IReadOnlyList<MotionOrder>> _orders = new();
    private readonly ConcurrentDictionary<long, long> _orderSequences = new();

    public long OrderSequence(long shipId) => _orderSequences.GetValueOrDefault(shipId);

    public IReadOnlyList<MotionOrder> Orders(long shipId, long after) =>
        _orders.GetValueOrDefault(shipId)?.Where(o => o.Sequence > after).ToArray() ?? [];

    public bool MissingOrders(long shipId, long after) =>
        _orders.TryGetValue(shipId, out var orders) && orders.Count > 0 && after < orders[0].Sequence - 1;

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
            _ => null,
        };
        if (target != shipId)
        {
            return;
        }
        using (EnterMotion())
        {
            long sequence = _orderSequences.GetValueOrDefault(shipId) + 1;
            _orderSequences[shipId] = sequence;
            _orders[shipId] = (_orders.GetValueOrDefault(shipId) ?? [])
                .Append(new MotionOrder(sequence, update))
                .Where(order => order.Update.Stamp >= update.Stamp - MovementHistoryTicks)
                .ToArray();
        }
    }

    private readonly ConcurrentDictionary<long, ShipMotion> _motion = new();
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
            _ships.TryRemove(shipId, out _);
            _motion.TryRemove(shipId, out _);
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
        _ships
            .Values.Where(ship =>
                ship.Pilot.SystemId == systemId
                && ship.Pilot.Position is not null
                && ship.Pilot.ShipGroupId != FleetDefinitions.CapsuleGroup
                && ship.Warp is null
                && ship.AlignTo is null
                && ship.DepartureGate is null
                && !ship.Arriving
                && SpatialStatus(ship.Pilot.ShipId).CloakModuleId == 0
                && ship.Health?.Remaining.Destroyed != true
            )
            .Select(ship => new NpcTargetCandidate(ship.Pilot.PilotId, ship.Pilot.ShipId))
            .ToArray();

    public ShipContact[] Peers(DockedPilot pilot)
    {
        return RunMotion(pilot.SystemId, () => PeersOwned(pilot), nameof(Peers));
    }

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
                        && !ship.Arriving
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

    private ShipContact[] PeersOwned(DockedPilot pilot)
    {
        using (EnterMotion(systemId: pilot.SystemId))
        {
            var offline =
                crimewatch
                    ?.LoggedOff(pilot.SystemId)
                    .Select(p => new ShipContact(PilotContact.From(p.Pilot), SpacePosition.Zero, null, 0, null))
                ?? [];
            return _ships
                .Values.Concat(offline)
                .GroupBy(s => s.Pilot.ShipId)
                .Select(g => g.First())
                .Where(ship =>
                    ship.Pilot.SystemId == pilot.SystemId
                    && ship.Pilot.ShipId != pilot.ShipId
                    && SpatialStatus(ship.Pilot.ShipId).CloakModuleId == 0
                    && pilot.Position is { } viewer
                    && ship.Pilot.Position is { } other
                    && viewer.DistanceTo(other) <= NavigationDefinitions.MaximumTargetDistance
                )
                .OrderBy(ship => ship.Pilot.ShipId)
                .Select(ExpireTimedDefenses)
                .ToArray();
        }
    }

    private ShipContact ExpireTimedDefenses(ShipContact ship)
    {
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
            if (!_ships.TryGetValue(shipId, out var ship))
            {
                return null;
            }
            return
                ship.Pilot.SystemId == systemId
                && ship.Pilot.Position is { } position
                && ship.Warp is null
                && ship.DepartureGate is null
                && !ship.Arriving
                && SpatialStatus(ship.Pilot.ShipId).CloakModuleId == 0
                && ship.Health?.Remaining.Destroyed != true
                ? new(shipId, position, ship.Velocity)
                : null;
        }
    }

    public PilotCombatPresence? Find(int systemId, long shipId)
    {
        return RunMotion(systemId, () => FindOwned(systemId, shipId), nameof(Find));
    }

    private PilotCombatPresence? FindOwned(int systemId, long shipId)
    {
        using (EnterMotion(systemId: systemId))
        {
            if (_ships.ContainsKey(shipId))
            {
                return FindOnline(systemId, shipId);
            }
            return crimewatch?.LoggedOff(systemId).FirstOrDefault(p => p.Pilot.ShipId == shipId)?.Pilot.Position is { } offline
                ? new(shipId, offline, SpacePosition.Zero)
                : null;
        }
    }
}
