using Eden.Gameplay.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

internal sealed record OwnerStationsReply(IReadOnlyList<StationDirectoryEntry> Stations) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            ["stationID", "solarSystemID"],
            Stations.Select(station => new OwnerStationRow(station)).ToArray()
        ).ToBlue();
}

internal sealed record OwnerStationRow(StationDirectoryEntry Station) : BlueModel
{
    public override object ToBlue() => new object?[] { Station.Id, Station.SystemId };
}

internal sealed record StationDirectoryReply(StationDirectorySnapshot Directory) : BlueModel
{
    private const int MaximumServiceBits = sizeof(long) * 8 - 1;

    public override object ToBlue()
    {
        var masks = ServiceMasks(Directory);
        return new CompatibilityRowset(
            [
                "stationID",
                "stationTypeID",
                "stationName",
                "solarSystemID",
                "constellationID",
                "regionID",
                "corporationID",
                "ownerID",
                "operationID",
                "orbitID",
                "x",
                "y",
                "z",
                "serviceMask",
            ],
            Directory
                .Stations.Select(station => new StationInfoRow(
                    station,
                    station.Services.Aggregate(0L, (mask, service) => mask | masks[service])
                ))
                .ToArray()
        ).ToBlue();
    }

    internal static long ServiceMask(StationDirectorySnapshot directory, StationDirectoryEntry station)
    {
        var masks = ServiceMasks(directory);
        return station.Services.Aggregate(0L, (mask, service) => mask | masks[service]);
    }

    private static Dictionary<int, long> ServiceMasks(StationDirectorySnapshot directory)
    {
        // Build 3662168 evestations.data.get_station_service_masks enumerates
        // sorted service IDs. IDs may have gaps: shifting by the ID is incorrect.
        var serviceIds = directory.ServiceIds.Distinct().Order().ToArray();
        if (serviceIds.Length > MaximumServiceBits)
        {
            throw new InvalidDataException("Too many catalog station services for a signed service mask.");
        }
        return serviceIds.Select((id, index) => (Id: id, Mask: 1L << index)).ToDictionary(service => service.Id, service => service.Mask);
    }
}

internal sealed record StationDetailsReply(StationDirectoryEntry Station, long ServiceMask) : BlueModel
{
    // The pinned location row's Station() returns an attribute-bearing object.
    // Public contract confirmation reads regionID; map consumers also update stationName.
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["stationID"] = Station.Id,
                ["stationTypeID"] = Station.TypeId,
                ["stationName"] = new BlueUnicode(Station.Name),
                ["solarSystemID"] = Station.SystemId,
                ["constellationID"] = Station.ConstellationId,
                ["regionID"] = Station.RegionId,
                ["corporationID"] = Station.OwnerId,
                ["ownerID"] = Station.OwnerId,
                ["operationID"] = Station.OperationId,
                ["orbitID"] = Station.OrbitId,
                ["x"] = Station.Position.X,
                ["y"] = Station.Position.Y,
                ["z"] = Station.Position.Z,
                ["serviceMask"] = ServiceMask,
            }
        );
}
