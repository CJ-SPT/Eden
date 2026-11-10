using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

// Immutable collision/route geometry and resource rows are job inputs. Loading
// them does not construct a native system scene or any observer contacts.
[Injectable]
internal sealed class CapsuleerGeometry(WorldDatabase database, IAsteroidField asteroids)
{
    internal IReadOnlyList<StaticSpaceObject> Load(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var live = LiveWorld.Find(database.Path);
        if (
            live is not null
            && live.SqlConnections == 0
            && live.TrafficGeometry.TryRead(systemId, catalog.Definitions.Identity.Generation, out var cached)
        )
        {
            return cached;
        }
        return [.. SystemObjects.Geometry(catalog, systemId), .. asteroids.List(systemId)];
    }

    internal static IReadOnlyList<StaticSpaceObject> Load(WorldDbContext world, CatalogDbContext catalog, int systemId, long now)
    {
        var live = LiveWorld.Find(world.WorldPath);
        if (
            live is not null
            && (
                live.SqlConnections == 0
                || live.SqlConnections == 1 && world.Database.GetDbConnection().State == System.Data.ConnectionState.Open
            )
            && !WorldConnections.HasChanges(world, "WorldAsteroid", "WorldBeltRenewal")
            && live.TrafficGeometry.TryRead(systemId, catalog.Definitions.Identity.Generation, out var cached)
        )
        {
            return cached;
        }
        AsteroidFieldStore.Populate(world, catalog, systemId, now);
        world.SaveChanges();
        return [.. SystemObjects.Geometry(catalog, systemId), .. AsteroidFieldStore.List(world, catalog, systemId)];
    }

    internal sealed record ResourcePreparation(
        int System,
        IReadOnlyList<int> Belts,
        IReadOnlyList<int> Missing,
        IReadOnlyList<StaticSpaceObject>? Resources
    );

    // Capture committed rows only; combine them with catalog geometry outside ownership.
    internal static ResourcePreparation? CaptureResources(
        WorldDatabase database,
        WorldDbContext world,
        CatalogDbContext catalog,
        int systemId
    )
    {
        var live = LiveWorld.Find(database.Path);
        if (live?.TrafficGeometry.TryRead(systemId, catalog.Definitions.Identity.Generation, out _) == true)
        {
            return null;
        }
        var belts = catalog.CelestialsInSystem(systemId).Where(b => b.GroupId == AsteroidDefinitions.BeltGroup).Select(b => b.Id).ToArray();
        // Recording ownership before first insertion preserves unrelated committed views.
        live?.TrafficGeometry.TrackBelts(systemId, belts);
        var populated =
            belts.Length == 0
                ? []
                : world.Set<WorldAsteroid>().Where(a => a.SystemId == systemId).Select(a => a.BeltId).Distinct().ToArray();
        var renewed =
            belts.Length == 0 ? [] : world.Set<SavedBeltRenewal>().Where(b => b.SystemId == systemId).Select(b => b.BeltId).ToArray();
        var missing = belts.Except(populated).ToArray();
        var resources = missing.Length == 0 && !belts.Except(renewed).Any() ? AsteroidFieldStore.List(world, catalog, systemId) : null;
        return new(systemId, Array.AsReadOnly(belts), Array.AsReadOnly(missing), resources);
    }

    internal static int[] Capture(WorldDatabase database, WorldDbContext world, CatalogDbContext catalog, int systemId)
    {
        var capture = CaptureResources(database, world, catalog, systemId);
        if (capture?.Resources is { } resources)
        {
            LiveWorld
                .Find(database.Path)
                ?.TrafficGeometry.Publish(
                    systemId,
                    catalog.Definitions.Identity.Generation,
                    capture.Belts,
                    [.. SystemObjects.Geometry(catalog, systemId), .. resources]
                );
        }
        return capture?.Missing.ToArray() ?? [];
    }
}
