using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class ShipAppearanceHandler(IPilotShipAppearance appearance) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "ship" || method is not ("GetDirtTimestamp" or "ResetDirtTimestamp"))
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (args is not [long shipId] || shipId <= 0 || keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Choose one accessible owned ship.");
            }
            var result =
                method == "ResetDirtTimestamp"
                    ? appearance.Clean(context.Pilot.PilotId, shipId)
                    : appearance.Read(context.Pilot.PilotId, shipId);
            if (method == "ResetDirtTimestamp")
            {
                context.Flight.RefreshDirtTimestamp(shipId, result.Timestamp);
            }
            return result.Timestamp;
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }
}
