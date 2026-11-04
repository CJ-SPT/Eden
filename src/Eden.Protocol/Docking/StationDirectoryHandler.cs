using Eden.Gameplay.Docking;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class StationDirectoryHandler(IStationDirectory stations) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service != "map" || method != "GetStationInfo")
        {
            return PilotCallResult.Unsupported;
        }
        if (args.Length != 0 || keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Station information does not accept arguments.");
        }
        return new StationDirectoryReply(stations.Read());
    }
}
