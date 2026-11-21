using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.FactionWarfare;

[Injectable]
internal sealed class ShipcasterDirectoryHandler : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            (service, method)
            is not (

                ("shipCasterLandingPadMgr", "GetFactionLandingPads" or "GetAllLandingPads")
                or
                ("shipCasterLauncherMgr", "GetFactionsWithShipcaster")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }

        bool validArguments = method == "GetFactionLandingPads" ? args is [long and > 0 and <= int.MaxValue] : args.Length == 0;
        if (!validArguments || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Invalid shipcaster directory arguments.");
        }

        // Build 3662168 iterates the pads and checks launcher membership to render its unbuilt state.
        return BlueEmpty.List;
    }
}
