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
        if (service == "stationSvc" && method == "GetStation")
        {
            if (args is not [long stationId] || stationId is <= 0 or > int.MaxValue || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Choose a valid station ID.");
            }
            var directory = stations.Read();
            return directory.Stations.FirstOrDefault(station => station.Id == stationId) is { } station
                ? new StationDetailsReply(station, StationDirectoryReply.ServiceMask(directory, station))
                : PilotCallResult.None;
        }
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
