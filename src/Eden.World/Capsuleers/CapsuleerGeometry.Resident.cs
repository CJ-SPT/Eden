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
}
