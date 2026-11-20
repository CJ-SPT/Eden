using Eden.Data.World.Economy;
using Eden.Data.World.Mining;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerGeometry
{
    internal static IReadOnlyList<StaticSpaceObject> Load(WorldAction world, CatalogDbContext catalog, int systemId, long now)
    {
        if (
            world is EconomyTransaction resident
            && !resident.HasPrivateRows<WorldAsteroid>()
            && !resident.HasPrivateRows<SavedBeltRenewal>()
            && LiveWorld.Find(world.WorldPath)?.TrafficGeometry.TryRead(systemId, catalog.Definitions.Identity.Generation, out var view)
                == true
        )
        {
            return view;
        }
        AsteroidFieldStore.Populate(world, catalog, systemId, now);
        world.SaveChanges();
        return [.. SystemObjects.Geometry(catalog, systemId), .. AsteroidFieldStore.List(world, catalog, systemId)];
    }

    internal static ResourcePreparation? CaptureResources(WorldDatabase database, WorldAction world, CatalogDbContext catalog, int systemId)
    {
        var live = LiveWorld.Find(database.Path);
        if (live?.TrafficGeometry.TryRead(systemId, catalog.Definitions.Identity.Generation, out _) == true)
        {
            return null;
        }
        var belts = catalog.CelestialsInSystem(systemId).Where(b => b.GroupId == AsteroidDefinitions.BeltGroup).Select(b => b.Id).ToArray();
        // Recording ownership before first insertion preserves unrelated committed views.
        live?.TrafficGeometry.TrackBelts(systemId, belts);
        var asteroids = world.AsteroidsInSystem(systemId).ToArray();
        var missing = belts.Except(asteroids.Select(a => a.BeltId)).ToArray();
        bool complete = missing.Length == 0 && (belts.Length == 0 || !belts.Except(world.RenewedBeltsInSystem(systemId)).Any());
        var resources = complete ? AsteroidFieldStore.List(catalog, asteroids) : null;
        return new(systemId, Array.AsReadOnly(belts), Array.AsReadOnly(missing), resources);
    }
}
