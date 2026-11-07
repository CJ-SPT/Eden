using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Drones;

public enum DroneDelegationMode
{
    None,
    Assist,
    Guard,
}

public sealed record DroneDelegation(
    DroneDelegationMode Mode,
    long PilotId,
    long ShipId,
    long FleetId,
    long OwnerMembership,
    long DelegateMembership,
    long ReactionRevision
);

public sealed record DroneDelegateMember(
    long PilotId,
    long ShipId,
    int SystemId,
    SpacePosition? Position,
    double Radius,
    bool Available,
    bool Capsule,
    FleetMembership? Membership
);

public static class DroneDelegationRules
{
    // Build 3662168 appConst.maxDroneAssist; shared by assist and guard.
    public const int MaximumAssigned = 20;

    public static void CheckAssignment(
        DroneDelegationMode mode,
        DroneDelegateMember owner,
        DroneDelegateMember member,
        double range,
        int assigned,
        int requested
    )
    {
        if (mode is not DroneDelegationMode.Assist and not DroneDelegationMode.Guard)
        {
            throw new InvalidDataException("Choose assist or guard.");
        }
        if (
            !owner.Available
            || owner.Position is null
            || !member.Available
            || member.Position is null
            || member.Capsule
            || member.PilotId == owner.PilotId
            || member.ShipId == owner.ShipId
            || member.SystemId != owner.SystemId
            || owner.Membership is null
            || member.Membership is null
            || owner.Membership.FleetId != member.Membership.FleetId
        )
        {
            throw new InvalidDataException("Assign drones to another present non-capsule ship in your fleet.");
        }
        double separation = owner.Position.DistanceTo(member.Position);
        if (!double.IsFinite(range) || range < 0 || !double.IsFinite(separation) || separation > Math.Min(range, FleetRules.CommandRange))
        {
            throw new InvalidDataException("The fleet member is outside drone control range or your local grid.");
        }
        if (assigned < 0 || requested <= 0 || requested > MaximumAssigned - assigned)
        {
            throw new InvalidDataException("A fleet member can receive at most twenty assisted or guarding drones.");
        }
    }

    public static bool Maintained(DroneDelegation delegation, DroneDelegateMember owner, DroneDelegateMember member) =>
        owner.Available
        && member.Available
        && !member.Capsule
        && owner.Position is { } from
        && member.Position is { } to
        && owner.SystemId == member.SystemId
        && member.PilotId == delegation.PilotId
        && member.ShipId == delegation.ShipId
        && owner.Membership is { } owned
        && owned.FleetId == delegation.FleetId
        && owned.Version == delegation.OwnerMembership
        && member.Membership is { } joined
        && joined.FleetId == delegation.FleetId
        && joined.Version == delegation.DelegateMembership
        && from.DistanceTo(to) <= FleetRules.CommandRange;

    public static DroneCombatTrigger Trigger(DroneDelegationMode mode, DroneCombatTriggers triggers) =>
        mode == DroneDelegationMode.Assist ? triggers.Offensive : triggers.Incoming;

    public static DroneContact React(DroneContact drone, DroneCombatTriggers triggers, long commandedAt, long now)
    {
        if (drone.Delegation is not { } delegation)
        {
            return drone;
        }
        var trigger = Trigger(delegation.Mode, triggers);
        if (trigger.Revision <= delegation.ReactionRevision || trigger.At > now)
        {
            return drone;
        }
        bool engage = trigger.At >= commandedAt && trigger.OtherId > 0 && trigger.OtherId != drone.ShipId;
        return drone with
        {
            Delegation = delegation with { ReactionRevision = trigger.Revision },
            Activity = engage ? DroneActivity.Fighting : drone.Activity,
            TargetId = engage ? trigger.OtherId : drone.TargetId,
        };
    }

    public static DroneContact Cancel(DroneContact drone) =>
        drone with
        {
            Delegation = null,
            Activity = DroneActivity.Idle,
            TargetId = drone.ShipId,
            Orbiting = false,
        };
}
