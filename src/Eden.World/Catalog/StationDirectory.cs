using System.Collections.Immutable;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Catalog;

[Injectable(InjectionType.Singleton)]
internal sealed class StationDirectory(WorldDatabase database) : IStationDirectory
{
    private static readonly CatalogDefinition<StationDirectorySnapshot> s_snapshot = new(
        "directory/stations",
        (catalog, _) => Load(catalog),
        Estimate: snapshot => 1024 + snapshot.Stations.Count * 512L
    );

    public StationDirectorySnapshot Read()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return catalog.Definition(s_snapshot, 0);
    }

    private static StationDirectorySnapshot Load(CatalogDbContext catalog)
    {
        var serviceIds = catalog.Records(CatalogData.Client.Stationservices).Keys.Select(id => checked((int)id)).Order().ToImmutableArray();
        var operations = catalog
            .Records(CatalogData.Client.Stationoperations)
            .ToDictionary(
                record => checked((int)record.Key),
                record => record.Value.Services?.Select(id => checked((int)id)).ToImmutableArray() ?? []
            );
        var stations = catalog
            .Stations()
            .OrderBy(station => station.Id)
            .Select(station => new StationDirectoryEntry(
                station.Id,
                station.TypeId,
                catalog.StationName(station.Id),
                station.SolarSystemId,
                catalog.RequireSystem(station.SolarSystemId).ConstellationId,
                station.RegionId,
                station.OwnerId,
                station.OperationId,
                station.OrbitId,
                new(station.X, station.Y, station.Z),
                operations.GetValueOrDefault(station.OperationId, [])
            ))
            .ToImmutableArray();
        return new(stations, serviceIds);
    }
}
