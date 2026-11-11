using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerGeometry
{
    internal static IReadOnlyList<StaticSpaceObject> Load(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        AsteroidFieldStore.Populate(world, catalog, systemId, now);
        world.SaveChanges();
        return [.. SystemObjects.Geometry(catalog, systemId), .. AsteroidFieldStore.List(world, catalog, systemId)];
    }
}
