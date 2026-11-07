using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class StationDirectory(WorldDatabase database) : IStationDirectory
{
    // The pinned catalog is immutable for the server lifetime. Do not query all
    // stations again for every pilot or retain a DbContext in shared infrastructure.
    private readonly Lazy<StationDirectorySnapshot> _stations = new(() => Load(database.CatalogPath));

    public StationDirectorySnapshot Read() => _stations.Value;

    private static StationDirectorySnapshot Load(string path)
    {
        using CatalogDbContext catalog = new(path);
        var serviceIds = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/stationservices")
            .Select(record => record.Key)
            .AsEnumerable()
            .Select(key => int.Parse(key, CultureInfo.InvariantCulture))
            .Order()
            .ToArray();
        var operations = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/stationoperations")
            .AsEnumerable()
            .ToDictionary(
                record => int.Parse(record.Key, CultureInfo.InvariantCulture),
                record => record.Read<ClientStationoperations>().Services?.Select(id => checked((int)id)).ToArray() ?? []
            );
        var stations = catalog
            .Set<Station>()
            .AsNoTracking()
            .Join(
                catalog.Set<StationDesignation>(),
                station => station.Id,
                designation => designation.StationId,
                (station, designation) =>
                    new
                    {
                        Station = station,
                        Designation = designation,
                        SystemName = station.SolarSystem.Name,
                        station.SolarSystem.ConstellationId,
                        station.SolarSystem.Constellation.RegionId,
                        OwnerName = station.Owner.Name,
                    }
            )
            .OrderBy(row => row.Station.Id)
            .AsEnumerable()
            .Select(row => new StationDirectoryEntry(
                row.Station.Id,
                row.Station.TypeId,
                row.Designation.Format(row.SystemName, row.OwnerName),
                row.Station.SolarSystemId,
                row.ConstellationId,
                row.RegionId,
                row.Station.OwnerId,
                row.Station.OperationId,
                row.Station.OrbitId,
                new(row.Station.X, row.Station.Y, row.Station.Z),
                operations.GetValueOrDefault(row.Station.OperationId) ?? []
            ))
            .ToArray();
        return new(stations, serviceIds);
    }
}
