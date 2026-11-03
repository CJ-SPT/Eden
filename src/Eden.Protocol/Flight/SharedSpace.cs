using System.Collections.Concurrent;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
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
    HardenerContact[]? Hardeners = null
);

[Injectable(InjectionType.Singleton)]
internal sealed class SharedSpace
{
    // Only immutable samples cross connection boundaries. Each pilot owns its simulation and socket.
    private readonly ConcurrentDictionary<long, ShipContact> _ships = new();

    private long _epoch = long.MinValue;

    // A Destiny stamp in a peer's ball record must refer to the same tick for both clients.
    public int Stamp(TimeProvider clock)
    {
        long now = clock.GetTimestamp();
        long epoch = Interlocked.CompareExchange(ref _epoch, now, long.MinValue);
        return epoch == long.MinValue ? 1 : 1 + (int)Math.Max(0, clock.GetElapsedTime(epoch, now).TotalSeconds);
    }

    public void Publish(ShipContact ship) => _ships[ship.Pilot.ShipId] = ship;

    public void Leave(long shipId) => _ships.TryRemove(shipId, out _);

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

    public ShipContact[] Peers(DockedPilot pilot) =>
        _ships
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
