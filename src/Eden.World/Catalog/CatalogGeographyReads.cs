using System.Collections.Immutable;
using Eden.Data.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Catalog;

public sealed partial class CatalogDbContext
{
    internal IEnumerable<CatalogStationDefinition> Stations() =>
        writable ? Set<Station>().Select(station => station.Id).ToArray().Select(RequireStation) : Definitions.Stations.Values;

    internal IEnumerable<CatalogStationDefinition> StationsInSystem(int id) =>
        writable
            ? Stations().Where(station => station.SolarSystemId == id).OrderBy(station => station.Id)
            : Definitions.StationsInSystem(id);

    internal IReadOnlyDictionary<int, int> StaticGateSystems() =>
        (
            from gate in Set<Stargate>()
            join body in Set<Celestial>() on gate.Id equals body.Id
            join item in Set<ItemType>() on body.TypeId equals item.Id
            select new { body.Id, body.SolarSystemId }
        ).ToDictionary(body => body.Id, body => body.SolarSystemId);

    internal IEnumerable<CatalogGateDefinition> GatesInSystem(int id) =>
        writable
            ? Set<Stargate>()
                .Where(gate => gate.SolarSystemId == id)
                .OrderBy(gate => gate.Id)
                .Select(gate => new CatalogGateDefinition(gate.Id, gate.SolarSystemId, gate.DestinationId, gate.Destination.SolarSystemId))
                .ToArray()
            : Definitions.GatesInSystem(id);

    internal string StationName(int id)
    {
        if (!writable)
        {
            return Definitions.StationNames[id];
        }
        var station = RequireStation(id);
        return Set<StationDesignation>()
            .Single(value => value.StationId == id)
            .Format(RequireSystem(station.SolarSystemId).Name, NpcCorporationName(station.OwnerId)!);
    }

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
