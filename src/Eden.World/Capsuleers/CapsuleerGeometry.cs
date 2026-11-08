using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

// Immutable collision/route geometry and resource rows are job inputs. Loading
// them does not construct a native system scene or any observer contacts.
[Injectable]
internal sealed class CapsuleerGeometry(WorldDatabase database, IAsteroidField asteroids)
{
    internal IReadOnlyList<StaticSpaceObject> Load(int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return [.. SystemObjects.Geometry(catalog, systemId), .. asteroids.List(systemId)];
    }

    internal static IReadOnlyList<StaticSpaceObject> Load(WorldDbContext world, CatalogDbContext catalog, int systemId, long now)
    {
        AsteroidFieldStore.Populate(world, catalog, systemId, now);
        world.SaveChanges();
        return [.. SystemObjects.Geometry(catalog, systemId), .. AsteroidFieldStore.List(world, catalog, systemId)];
    }
}
