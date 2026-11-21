using Eden.Gameplay.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Flight;

internal sealed partial class FlightSession
{
    private long _announcedGateCloak;

    private void SynchronizeGateCloak()
    {
        long deadline = GateCloaked ? SpatialStatus.GateCloakUntil : 0;
        if (!InBallpark || deadline == _announcedGateCloak)
        {
            return;
        }
        long previous = _announcedGateCloak;
        _announcedGateCloak = deadline;
        if (deadline != 0)
        {
            NotificationsBeforeFlight.Add(new JumpCloakUpdated(Pilot.ShipId, deadline));
        }
        else if (previous != 0)
        {
            if (!ModuleCloaked)
            {
                Update(new DestinyCommand.UncloakBall(Pilot.ShipId));
                Update(new DestinyCommand.SpecialEffect(Pilot.ShipId, null, null, null, null, "effects.Uncloak", false, true, false));
            }
            NotificationsBeforeFlight.Add(new JumpCloakCancelled(Pilot.ShipId));
        }
    }

    internal void CancelGateCloak()
    {
        if (GateCloaked)
        {
            space.CancelGateCloak(Pilot.PilotId, Pilot.ShipId);
            _travel.EndArrival();
            SynchronizeGateCloak();
            PublishMotion();
            PublishContact();
        }
    }

    internal void PrepareGateCloakModule(long module)
    {
        if (GateCloaked && !space.PreservesGateCloak(Pilot.PilotId, Pilot.ShipId, module))
        {
            CancelGateCloak();
        }
    }
}

internal sealed record JumpCloakUpdated(long ShipId, long EndsAt) : PilotNotification("OnJumpCloakUpdated")
{
    public override object?[] Arguments => [ShipId, EndsAt, GateTravelRules.CloakDurationTicks];
}

internal sealed record JumpCloakCancelled(long ShipId) : PilotNotification("OnJumpCloakCancelled")
{
    public override object?[] Arguments => [ShipId];
}
