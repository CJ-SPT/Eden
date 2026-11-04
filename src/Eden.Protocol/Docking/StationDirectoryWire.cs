using Eden.Gameplay.Docking;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Docking;

internal sealed record StationDirectoryReply(StationDirectorySnapshot Directory) : BlueModel
{
    private const int MaximumServiceBits = sizeof(long) * 8 - 1;

    public override object ToBlue()
    {
        // Build 3662168 evestations.data.get_station_service_masks enumerates
        // sorted service IDs. IDs may have gaps: shifting by the ID is incorrect.
        var serviceIds = Directory.ServiceIds.Distinct().Order().ToArray();
        if (serviceIds.Length > MaximumServiceBits)
        {
            throw new InvalidDataException("Too many catalog station services for a signed service mask.");
        }
        var masks = serviceIds
            .Select((id, index) => (Id: id, Mask: 1L << index))
            .ToDictionary(service => service.Id, service => service.Mask);
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
}
