using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Fleets;
using Eden.Gameplay.Flight;

namespace Eden.World.Runtime.Combat;

internal sealed partial class LiveCombatRuntime
{
    private DroneTarget? AdvanceDelegation(
        LiveDrone original,
        LiveCombatShip ownerShip,
        IPilotCombatPresence presence,
        ICooperativeFleets fleets,
        long now,
        out LiveDrone drone
    )
    {
        drone = original;
        if (original.Contact.Delegation is not { } delegation)
        {
            return null;
        }
        var online = presence.FindOnline(ownerShip.SystemId, delegation.ShipId);
        var player = online is null ? null : DronePlayerFor(delegation.ShipId);
        var memberShip = player?.SystemId == ownerShip.SystemId ? Ship(player.PilotId, now) : null;
        DroneDelegateMember ownerMember = new(
            ownerShip.PilotId,
            ownerShip.ShipId,
            ownerShip.SystemId,
            ownerShip.Position,
            ownerShip.Radius,
            !ownerShip.Health.At(now).Destroyed,
            false,
            fleets.Membership(ownerShip.PilotId)
        );
        DroneDelegateMember member = new(
            delegation.PilotId,
            memberShip?.ShipId ?? 0,
            memberShip?.SystemId ?? 0,
            online?.Position,
            memberShip?.Radius ?? 0,
            player?.PilotId == delegation.PilotId && memberShip?.Health.At(now).Destroyed == false,
            player?.Capsule == true,
            fleets.Membership(delegation.PilotId)
        );
        if (!DroneDelegationRules.Maintained(delegation, ownerMember, member))
        {
            drone = original with { Contact = DroneDelegationRules.Cancel(original.Contact), CommandedAt = now };
            return null;
        }
        drone = original with
        {
            Contact = DroneDelegationRules.React(original.Contact, DroneTriggers(delegation.ShipId), original.CommandedAt, now),
        };
        return new(delegation.ShipId, member.Position!, online!.Velocity, member.Radius, 0);
    }
}
