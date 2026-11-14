using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Fleets;

// Build 3662168 evefleet.const. Boss is a job independent of command rank.
public enum FleetRole
{
    Commander = 1,
    WingCommander = 2,
    SquadCommander = 3,
    Member = 4,
}

public enum FleetBroadcastScope
{
    Down = 1,
    Up = 2,
    All = 3,
}

public enum FleetBroadcastRange
{
    Universe,
    System,
    Bubble,
}

public enum FleetBroadcastKind
{
    Location,
    HoldPosition,
    TravelTo,
    JumpTo,
    AlignTo,
    NeedBackup,
    WarpTo,
    HealCapacitor,
    HealShield,
    HealArmor,
    Target,
    EnemySpotted,
    InPosition,
    HealTarget,
}

public sealed record FleetPilot(
    long Id,
    int CorporationId,
    long ShipId,
    int ShipTypeId,
    int SystemId,
    SpacePosition? Position,
    bool InTransit = false
);

public sealed record FleetPlacement(long WingId, long SquadId, FleetRole Role);

public sealed record FleetIdentity(long FleetId, long WingId, long SquadId, FleetRole Role, int Job);

public sealed record FleetMembership(long FleetId, long Version);

public sealed record FleetMember(FleetPilot Pilot, FleetPlacement Placement, int Job, bool TakesFleetWarp = true);

public sealed record FleetSquad(long Id, string Name);

public sealed record FleetWing(long Id, string Name, IReadOnlyList<FleetSquad> Squads);

public sealed record CooperativeFleet(
    long Id,
    IReadOnlyList<FleetMember> Members,
    IReadOnlyList<FleetWing> Wings,
    bool FreeMove,
    string Motd
);

public sealed record FleetBroadcast(FleetBroadcastKind Kind, FleetBroadcastScope Scope, FleetPilot Sender, long? ItemId, int? TypeId);

public sealed record FleetWarpOrder(
    long FleetId,
    long SenderId,
    long ShipId,
    int SystemId,
    long TargetId,
    double Range,
    SpacePosition? Point,
    SpacePosition Origin,
    long ControlVersion,
    long ExpiresAt
);

public abstract record FleetEvent
{
    public sealed record Invitation(long FleetId, long InviterId) : FleetEvent;

    public sealed record Joined(FleetMember Member) : FleetEvent;

    public sealed record Left(long PilotId) : FleetEvent;

    public sealed record Changed(FleetMember Before, FleetMember After, bool OnlyMember) : FleetEvent;

    public sealed record Hierarchy(long WingId, long? SquadId, bool Deleted, string? Name = null) : FleetEvent;

    public sealed record Options(bool Before, bool After) : FleetEvent;

    public sealed record Motd(string Text) : FleetEvent;

    public sealed record Broadcast(FleetBroadcast Value) : FleetEvent;

    public sealed record Warp(FleetWarpOrder Order) : FleetEvent;

    public sealed record MissionStanding(long SourceId, double Value) : FleetEvent;

    public sealed record Resync(FleetMember? Member) : FleetEvent;
}

public sealed record FleetDelivery(long FleetId, FleetEvent Event);

public interface ICooperativeFleets
{
    void Update(Guid session, FleetPilot? pilot);
    FleetIdentity? Identity(long actor);
    IReadOnlySet<long> MembersInFleet(long actor, IReadOnlyList<long> candidates);
    FleetMembership? Membership(long actor);
    FleetMembership? JoinMembership(long actor);
    IReadOnlyList<FleetPilot> Fleetmates(long actor);
    bool CanShareLoot(long actor, long owner, int system);
    IReadOnlyList<FleetDelivery> Drain(long actor);
    void NotifyMissionStanding(long pilotId, long sourceId, double value);
    CooperativeFleet Create(long actor);
    bool CanBind(long actor, long fleet);
    CooperativeFleet Read(long actor, long fleet);
    void Invite(long actor, long fleet, long target, FleetPlacement placement);
    void Accept(long actor, long fleet);
    void Reject(long actor, long fleet);
    void Leave(long actor, long fleet);
    void Kick(long actor, long fleet, long target);
    void Disband(long actor, long fleet);
    void Move(long actor, long fleet, long target, FleetPlacement placement);
    void MakeBoss(long actor, long fleet, long target);
    long CreateWing(long actor, long fleet);
    long CreateSquad(long actor, long fleet, long wing);
    void ChangeGroup(long actor, long fleet, long group, bool squad, string? name);
    void SetFreeMove(long actor, long fleet, bool enabled);
    void SetMotd(long actor, long fleet, string text);
    void SetTakesWarp(long actor, long fleet, bool enabled);
    void Broadcast(
        long actor,
        long fleet,
        FleetBroadcastKind kind,
        FleetBroadcastScope scope,
        FleetBroadcastRange range,
        long? item,
        int? type
    );
    void Warp(long actor, long fleet, long target, double range, SpacePosition? point);
    bool CanExecuteWarp(long actor, FleetWarpOrder order);
    void CancelWarpOrders(long actor);
}

public static class FleetRules
{
    public const long NoGroup = -1;
    public const int BossJob = 2;
    public const int MaximumMembers = 256;
    public const int MaximumWings = 25;
    public const int MaximumSquads = 25;
    public const int MaximumNameLength = 10;
    public const int MaximumMotdLength = 4000;
    public const int MaximumPendingEvents = 256;

    // Reuse the current world observer grid for fleet commands and bubble broadcasts.
    public const double CommandRange = NavigationDefinitions.MaximumTargetDistance;
    public const double MaximumWarpRange = 100000;
    public static readonly TimeSpan InvitationLifetime = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan WarpOrderLifetime = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan BroadcastInterval = TimeSpan.FromSeconds(1);

    public static bool Subordinate(FleetMember commander, FleetMember member) =>
        commander.Pilot.Id == member.Pilot.Id
        || commander.Placement.Role == FleetRole.Commander
        || commander.Placement.Role == FleetRole.WingCommander
            && member.Placement.WingId == commander.Placement.WingId
            && member.Placement.Role > FleetRole.WingCommander
        || commander.Placement.Role == FleetRole.SquadCommander
            && member.Placement.SquadId == commander.Placement.SquadId
            && member.Placement.Role == FleetRole.Member;

    public static bool InCommandRange(FleetPilot commander, FleetPilot member) =>
        commander.SystemId == member.SystemId
        && commander.Position is { } from
        && member.Position is { } to
        && from.DistanceTo(to) <= CommandRange;
}
