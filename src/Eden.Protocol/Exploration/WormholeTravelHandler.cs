using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Exploration;

[Injectable]
internal sealed class WormholeTravelHandler(IWormholeTravel travel, ISystemObjects systems, TimeProvider clock) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "wormholeMgr" || method is not ("WormholeJump" or "GetWormholePolarization"))
        {
            return PilotCallResult.Unsupported;
        }
        if (args is not [long entrance] || !WormholeRules.IsEndpoint(entrance) || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid wormhole request.");
        }
        if (method == "GetWormholePolarization")
        {
            var result = travel.Polarization(context.Pilot.PilotId, context.Pilot.SystemId, entrance);
            return result is null ? PilotCallResult.None : new PolarizationReply(result);
        }
        try
        {
            context.Flight.StartWormholeJump(entrance, travel, systems);
            long readyAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc() + TimeSpan.TicksPerSecond * Flight.FlightSession.JumpDurationTicks;
            return new JumpReply(context.Reference("beyonce", PilotBindingLifetime.Location).Id, readyAt);
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private sealed record JumpReply(string BindingId, long ReadyAt) : BlueModel
    {
        public override object ToBlue() => new object[] { BindingId, ReadyAt };
    }

    private sealed record PolarizationReply(WormholePolarization Polarization) : BlueModel
    {
        public override object ToBlue() => new object[] { Polarization.EndsAt, Polarization.Seconds };
    }
}

internal sealed record WormholeJumpCancelled() : PilotNotification("OnWormholeJumpCancel")
{
    public override object?[] Arguments => [];
}
