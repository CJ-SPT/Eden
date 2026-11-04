using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal sealed record StoredShipManifest(
    long Id,
    int TypeId,
    string Name,
    int FlagId,
    IReadOnlyList<OwnedItem> Items,
    ShipHealthState? Health
);

internal static class ShipStoragePersistence
{
    internal static ShipStorageLocation Location(Ship ship) =>
        new(
            ship.Id,
            ship.OwnerId,
            ship.StoredInId ?? (ship.Position is null ? ship.StationId : ship.Position.SystemId ?? ship.StationId),
            ship.StoredInId is not null ? ship.StorageFlagId
                : ship.Position is null ? (int)InventoryFlag.Hangar
                : (int)InventoryFlag.AutoFit
        );

    internal static IReadOnlyList<OwnedItem> Contents(WorldDbContext world, long pilotId, long shipId)
    {
        var modules = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == shipId).Select(i => i.Id).ToArray();
        return world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && (i.LocationId == shipId || modules.Contains(i.LocationId)))
            .OrderBy(i => i.Id)
            .ToArray();
    }

    internal static StoredShipManifest Manifest(WorldDbContext world, Ship ship) =>
        new(
            ship.Id,
            ship.TypeId,
            ship.Name,
            ship.StorageFlagId,
            Contents(world, ship.OwnerId, ship.Id),
            world.Set<ShipHealthState>().Find(ship.Id)
        );

    internal static void Destroy(WorldDbContext world, Ship ship)
    {
        PilotInsuranceStore.VoidRemovedHull(world, ship.Id);
        world.RemoveRange(Contents(world, ship.OwnerId, ship.Id));
        world.Remove(ship);
    }

    internal static void Check(WorldDbContext world)
    {
        foreach (var stored in world.Ships.Where(s => s.StoredInId != null).Include(s => s.Position).ToArray())
        {
            var parent = world.Ships.Find(stored.StoredInId!.Value);
            var wreck = parent is null ? world.Set<WorldWreck>().Find(stored.StoredInId.Value) : null;
            bool validParent = parent is not null
                ? parent.OwnerId == stored.OwnerId
                    && parent.StoredInId is null
                    && ShipHoldRules.Definition(stored.StorageFlagId)?.AllowsAssembledShip == true
                : wreck?.OwnerId == stored.OwnerId && stored.StorageFlagId == (int)InventoryFlag.Cargo;
            if (!validParent || stored.Position is not null || world.Pilots.Any(p => p.ActiveShipId == stored.Id))
            {
                throw new InvalidDataException("The save contains an invalid stored ship location.");
            }
        }
    }
}
