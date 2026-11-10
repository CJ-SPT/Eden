using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

// Callers own the transaction and feature-specific eligibility/stack-selection policy.
internal static class InventoryPersistence
{
    internal static OwnedItem? OwnedStack(WorldDbContext world, long pilotId, long id) =>
        world.Set<OwnedItem>().Find(id) is { } item && item.OwnerId == pilotId ? item : null;

    internal static double UsedVolume(WorldDbContext world, CatalogDbContext catalog, long pilotId, long location, int flag)
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
        WorldDbContext world,
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

    internal static long NextId(WorldDbContext world)
    {
        // Items can become ships or installed implants without changing identity.
        // All inventory/body/booster sequences retain high-water IDs after deletion.
        long last = world
            .Database.SqlQueryRaw<long>(
                "SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence "
                    + "WHERE name IN ('WorldItem','WorldShip','WorldPilotImplant','WorldBooster','WorldStructure')"
            )
            .AsEnumerable()
            .Single();
        long tracked = world
            .ChangeTracker.Entries<OwnedItem>()
            .Select(e => e.Entity.Id)
            .Concat(world.ChangeTracker.Entries<Ship>().Select(e => e.Entity.Id))
            .Concat(world.ChangeTracker.Entries<SavedPilotImplant>().Select(e => e.Entity.ItemId))
            .Concat(world.ChangeTracker.Entries<SavedBooster>().Select(e => e.Entity.ItemId))
            .Concat(world.ChangeTracker.Entries<Eden.Data.World.Structures.SavedStructure>().Select(e => e.Entity.Id))
            .DefaultIfEmpty(0)
            .Max();
        return checked(Math.Max(Math.Max(last, tracked), InventoryDefinitions.FirstOwnedItemId) + 1);
    }
}
