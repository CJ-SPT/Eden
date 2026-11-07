using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Maps;

[Injectable]
internal sealed class MapStartupHandler : ISelectedPilotCallHandler
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

                ("structureDirectory", "GetJumpBridgesWithMyAccess")
                or
                ("stationSvc", "GetAllianceSystems")
                or
                ("fwWarzoneSolarsystem", "GetAllWarzonesOccupationStates" or "GetAllWarzonesOccupationStatesUncached")
            )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Map startup information does not accept arguments.");
        }

        // Eden has no player jump bridges, alliance sovereignty or faction-war occupations.
        // The client retains its own static universe and NPC faction ownership data.
        return service switch
        {
            "structureDirectory" => new EmptyJumpBridgeAccessReply(),
            "stationSvc" => BlueEmpty.List,
            _ => BlueEmpty.IndexedDictionary,
        };
    }
}

internal sealed record EmptyJumpBridgeAccessReply : BlueModel
{
    // Build 3662168 MapView.LoadAllJumpBridges unpacks these three values before
    // iterating bridge endpoint pairs and checking structure-ID access membership.
    public override object ToBlue() => new object?[] { BlueEmpty.List, BlueEmpty.List, BlueEmpty.List };
}
