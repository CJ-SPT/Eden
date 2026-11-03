using Eden.Gameplay.Inventory;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

// Callers own the transaction and feature-specific eligibility/stack-selection policy.
internal static class InventoryPersistence
{
    internal static OwnedItem? OwnedStack(WorldDbContext world, long pilotId, long id) =>
        world.Set<OwnedItem>().Find(id) is { } item && item.OwnerId == pilotId ? item : null;

    internal static double UsedVolume(WorldDbContext world, CatalogDbContext catalog, long pilotId, long location, int flag) =>
        world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == location)
            .AsEnumerable()
            .Concat(world.Set<OwnedItem>().Local)
            .DistinctBy(i => i.Id)
            .Where(i => i.OwnerId == pilotId && i.LocationId == location && i.FlagId == flag && world.Entry(i).State != EntityState.Deleted)
            .Sum(i => PilotInventoryStore.Volume(catalog, i.TypeId) * i.Quantity);

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
        // Packaged items become assembled ships without changing identity. Both
        // AUTOINCREMENT sequences retain their high-water IDs after deletion.
        long last = world
            .Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence WHERE name IN ('WorldItem','WorldShip')")
            .AsEnumerable()
            .Single();
        long tracked = world
            .ChangeTracker.Entries<OwnedItem>()
            .Select(e => e.Entity.Id)
            .Concat(world.ChangeTracker.Entries<Ship>().Select(e => e.Entity.Id))
            .DefaultIfEmpty(0)
            .Max();
        return checked(Math.Max(Math.Max(last, tracked), InventoryDefinitions.FirstOwnedItemId) + 1);
    }
}
