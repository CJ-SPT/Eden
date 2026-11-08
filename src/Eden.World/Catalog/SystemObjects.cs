using System.Collections.Immutable;
using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Storage;

namespace Eden.World.Catalog;

[Injectable]
internal sealed class SystemObjects(WorldDatabase database, IAsteroidField asteroids) : ISystemObjects
{
    private const long ObjectEstimatedBytes = 192;
    private static readonly CatalogDefinition<ImmutableArray<StaticSpaceObject>> s_objects = new(
        "system-static-objects",
        LoadStatic,
        Estimate: objects => 256 + objects.Length * ObjectEstimatedBytes
    );

    internal static FlightSystem FindSystem(CatalogDbContext catalog, int systemId)
    {
        var system = catalog.RequireSystem(systemId);
        return new(system.Id, system.ConstellationId, system.RegionId);
    }

    public FlightSystem GetSystem(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return FindSystem(catalog, systemId);
    }

    public IReadOnlyList<StaticSpaceObject> Load(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return [.. catalog.Definition(s_objects, systemId), .. asteroids.List(systemId)];
    }

    internal static IReadOnlyList<StaticSpaceObject> Load(WorldDbContext world, CatalogDbContext catalog, int systemId, long now)
    {
        Eden.World.Mining.AsteroidFieldStore.Populate(world, catalog, systemId, now);
        world.SaveChanges();
        return [.. catalog.Definition(s_objects, systemId), .. Eden.World.Mining.AsteroidFieldStore.List(world, catalog, systemId)];
    }

    private static ImmutableArray<StaticSpaceObject> LoadStatic(CatalogDbContext catalog, int systemId)
    {
        var gates = catalog.Definitions.GatesInSystem(systemId).ToDictionary(gate => gate.Id);
        List<StaticSpaceObject> objects = [];
        foreach (var body in catalog.CelestialsInSystem(systemId))
        {
            if (catalog.FittingType(body.TypeId) is not { } type)
            {
                continue;
            }
            gates.TryGetValue(body.Id, out var gate);
            objects.Add(
                new(
                    body.Id,
                    body.TypeId,
                    body.GroupId,
                    type.CategoryId,
                    1,
                    new(body.X, body.Y, body.Z),
                    (float)(body.Radius ?? type.Radius ?? 0),
                    gate?.DestinationId,
                    gate?.DestinationSystemId
                )
            );
        }
        foreach (var station in catalog.Definitions.StationsInSystem(systemId))
        {
            if (catalog.FittingType(station.TypeId) is { } type)
            {
                objects.Add(
                    new(
                        station.Id,
                        station.TypeId,
                        type.GroupId,
                        type.CategoryId,
                        station.OwnerId,
                        new(station.X, station.Y, station.Z),
                        (float)(type.Radius ?? 0)
                    )
                );
            }
        }
        // Secondary suns and disrupted gates are outside the mapObjects projection.
        var content = catalog.Read(CatalogData.Client.Solarsystemcontent, (long)systemId);
        if (content?.SecondarySun is { } sun)
        {
            AddSystemObject(checked((int)sun.ItemID!.Value), sun.TypeID, sun.Position);
        }
        if (content?.DisruptedStargates is { } disrupted)
        {
            foreach (var (key, gate) in disrupted)
            {
                AddSystemObject(int.Parse(key, CultureInfo.InvariantCulture), gate.TypeID, gate.Position);
            }
        }
        return objects.ToImmutableArray();

        void AddSystemObject(int id, long? typeId, ClientConstellationsCenter? point)
        {
            if (point is not { X: double x, Y: double y, Z: double z } || typeId is null)
            {
                throw new InvalidDataException($"Incomplete static object {id} in system {systemId}.");
            }
            var type = catalog.FittingType(typeId.Value) ?? throw new InvalidDataException($"Missing static object type {typeId}.");
            objects.Add(new(id, type.Id, type.GroupId, type.CategoryId, 1, new(x, y, z), (float)(type.Radius ?? 0)));
        }
    }
}
