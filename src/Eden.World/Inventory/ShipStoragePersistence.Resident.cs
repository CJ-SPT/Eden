using Eden.Data.World.Combat;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Loot;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.World.Combat;
using Eden.World.Corporations;
using Eden.World.Insurance;
using Eden.World.Loot;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static partial class ShipStoragePersistence
{
    internal static IReadOnlyList<OwnedItem> Contents(WorldAction world, long pilotId, long shipId)
    {
        var modules = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == shipId).Select(i => i.Id).ToArray();
        return world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && (i.LocationId == shipId || Enumerable.Contains(modules, i.LocationId)))
            .OrderBy(i => i.Id)
            .ToArray();
    }

    internal static StoredShipManifest Manifest(WorldAction world, Ship ship) =>
        new(
            ship.Id,
            ship.TypeId,
            ship.Name,
            ship.StorageFlagId,
            Contents(world, ship.OwnerId, ship.Id),
            world.Set<ShipHealthState>().Find(ship.Id)
        );

    internal static void Destroy(WorldAction world, Ship ship)
    {
        PilotInsuranceStore.VoidRemovedHull(world, ship.Id);
        world.RemoveRange(Contents(world, ship.OwnerId, ship.Id));
        world.Remove(ship);
    }
}
