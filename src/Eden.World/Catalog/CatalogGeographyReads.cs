using System.Collections.Immutable;
using Eden.Data.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
    internal CatalogStationDefinition? FindStation(long id)
    {
        if (!writable)
        {
            return Definitions.FindStation(id);
        }
        if (id is < int.MinValue or > int.MaxValue || Set<Station>().Find((int)id) is not { } station)
        {
            return null;
        }
        return new(
            station.Id,
            station.TypeId,
            station.OwnerId,
            station.SolarSystemId,
            station.OperationId,
            station.OrbitId,
            station.X,
            station.Y,
            station.Z,
            RequireSystem(station.SolarSystemId).RegionId
        );
    }

    internal CatalogStationDefinition RequireStation(int id) => FindStation(id) ?? throw new InvalidDataException($"Missing station {id}.");

    internal CatalogSystemDefinition? FindSystem(int id)
    {
        if (!writable)
        {
            return Definitions.Systems.GetValueOrDefault(id);
        }
        var system = Set<SolarSystem>().Find(id);
        return system is null
            ? null
            : new(system.Id, system.Name, system.ConstellationId, Set<Constellation>().Find(system.ConstellationId)!.RegionId);
    }

    internal CatalogSystemDefinition RequireSystem(int id) => FindSystem(id) ?? throw new InvalidDataException($"Missing system {id}.");

    internal CatalogCelestialDefinition? FindCelestial(long id) =>
        writable
            ? CatalogProvider.CelestialRows(Set<Celestial>().Where(body => body.Id == id)).SingleOrDefault()
            : Definitions.FindCelestial(id);

    internal ImmutableArray<CatalogCelestialDefinition> CelestialsInSystem(int id) =>
        writable
            ? CatalogProvider
                .CelestialRows(Set<Celestial>().Where(body => body.SolarSystemId == id).OrderBy(body => body.Id))
                .ToImmutableArray()
            : Definitions.CelestialsInSystem(id);
}
