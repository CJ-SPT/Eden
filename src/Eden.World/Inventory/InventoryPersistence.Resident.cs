using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static partial class InventoryPersistence
{
    internal static OwnedItem? OwnedStack(EconomyTransaction world, long pilotId, long id) =>
        world.Set<OwnedItem>().Find(id) is { } item && item.OwnerId == pilotId ? item : null;

    internal static double UsedVolume(EconomyTransaction world, CatalogDbContext catalog, long pilotId, long location, int flag)
    {
        var hold = ShipHoldRules.Definition(flag) ?? throw new InvalidDataException("Unknown inventory hold.");
        double items = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == location)
            .AsEnumerable()
            .Concat(world.Set<OwnedItem>().Local)
            .DistinctBy(i => i.Id)
            .Where(i => i.OwnerId == pilotId && i.LocationId == location && i.FlagId == flag && world.Entry(i).State != EntityState.Deleted)
            .Sum(i => hold.RequiredCapacity(PilotInventoryStore.Read(catalog, i), i.Quantity));
        double ships = world
            .Ships.Where(s => s.OwnerId == pilotId && s.StoredInId == location)
            .AsEnumerable()
            .Concat(world.Ships.Local)
            .DistinctBy(s => s.Id)
            .Where(s =>
                s.OwnerId == pilotId && s.StoredInId == location && s.StorageFlagId == flag && world.Entry(s).State != EntityState.Deleted
            )
            .Sum(s => hold.RequiredCapacity(PilotFleetStore.Read(catalog, s), 1));
        return items + ships;
    }

    internal static InventoryChange MergeOutput(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilotId,
        long location,
        int flag,
        int typeId,
        int quantity
    )
    {
        if (quantity <= 0)
        {
            throw new InvalidDataException("Output quantity must be positive.");
        }
        var stack = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == location && i.FlagId == flag && i.TypeId == typeId && !i.Singleton)
            .AsEnumerable()
            .Concat(world.Set<OwnedItem>().Local)
            .DistinctBy(i => i.Id)
            .Where(i =>
                i.OwnerId == pilotId
                && i.LocationId == location
                && i.FlagId == flag
                && i.TypeId == typeId
                && !i.Singleton
                && world.Entry(i).State != EntityState.Deleted
            )
            .OrderBy(i => i.Id)
            .FirstOrDefault();
        var previous = stack is null ? null : PilotInventoryStore.Read(catalog, stack);
        stack ??= world
            .Add(
                new OwnedItem
                {
                    Id = NextId(world),
                    OwnerId = pilotId,
                    LocationId = location,
                    FlagId = flag,
                    TypeId = typeId,
                }
            )
            .Entity;
        stack.Quantity = checked(stack.Quantity + quantity);
        return new(PilotInventoryStore.Read(catalog, stack), previous);
    }

    internal static long NextId(EconomyTransaction world) => world.NextItemId();
}
