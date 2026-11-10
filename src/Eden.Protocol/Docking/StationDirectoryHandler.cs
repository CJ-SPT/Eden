using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

[Injectable]
internal sealed class StationDirectoryHandler(IStationDirectory stations) : ISelectedPilotCallHandler
{
    // Pinned build 3662168 structures/services.pyj SERVICE_DOCKING, distinct from station service IDs.
    private const long StructureDockingService = 1;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (service == "structureSettings" && method == "CharacterHasService")
        {
            if (args is not [long locationId, long serviceId] || locationId < 0 || serviceId <= 0 || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Choose a valid location and structure service.");
            }
            // NPC docking is available. Player structures and their ACL/service state are not implemented.
            // A stale zero endpoint also returns a boolean denial, rather than an unsupported RPC error.
            return serviceId == StructureDockingService
                && locationId is > 0 and <= int.MaxValue
                && stations.Read().Stations.Any(station => station.Id == locationId);
        }
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
        if (
            (service == "stationSvc" && method == "GetStationsForOwner")
            || (service == "config" && method == "GetStationSolarSystemsByOwner")
        )
        {
            if (args is not [long ownerId] || ownerId is <= 0 or > int.MaxValue || keywords is { Count: > 0 })
            {
                return new PilotCallResult.Failure("Choose a valid station owner ID.");
            }
            // Pinned LPStoreSvc.GetNearestDockableWithLPStoreForCorp groups these
            // attribute-bearing rows by solarSystemID, then compares route lengths.
            var owned = stations.Read().Stations.Where(station => station.OwnerId == ownerId).ToArray();
            return method == "GetStationSolarSystemsByOwner"
                ? new CorporationStationSystemsReply(owned.Select(station => station.SystemId).Distinct().Order().ToArray())
                : new OwnerStationsReply(owned);
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
