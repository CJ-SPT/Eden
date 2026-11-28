using Eden.Data.World.Characters;
using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Corporations;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static partial class InventoryPersistence
{
    internal static OwnedItem? OwnedStack(WorldAction world, long pilotId, long id) =>
        world.Set<OwnedItem>().Find(id) is { } item && item.OwnerId == pilotId ? item : null;

    internal static double UsedVolume(WorldAction world, CatalogDbContext catalog, long pilotId, long location, int flag)
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
        WorldAction world,
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
                && !i.Online
                && i.VolumeOverride == null
                && i.CrystalDamage == 0
                && i.Quantity <= int.MaxValue - quantity
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

    internal static void StackDepositedOre(EconomyTransaction world, CatalogDbContext catalog, IReadOnlyList<InventoryChange> deposits)
    {
        var items = (EconomySet<OwnedItem, EconomyOwnedItem>)world.Set<OwnedItem>();
        var escrow = world.Set<SavedContractEscrow>();
        var depositedIds = deposits.Select(deposit => deposit.Item.Id).ToHashSet();
        foreach (var deposit in deposits)
        {
            var ore = deposit.Item;
            if (
                ore is not { CategoryId: AsteroidDefinitions.Category, FlagId: (int)InventoryFlag.Hangar, Quantity: > 0 }
                || items.Find(ore.Id) is not { Singleton: false, Online: false } incoming
            )
            {
                continue;
            }

            // Keep existing hangar rows and materialize only the compatible stack that receives this deposit.
            var target = items
                .IndexedSnapshots(nameof(OwnedItem.OwnerId), ore.OwnerId)
                .Where(stack =>
                    stack.OwnerId == ore.OwnerId
                    && stack.LocationId == ore.LocationId
                    && stack.FlagId == ore.FlagId
                    && stack.TypeId == ore.TypeId
                    && !stack.Singleton
                    && !stack.Online
                    && stack.VolumeOverride == incoming.VolumeOverride
                    && stack.CrystalDamage == incoming.CrystalDamage
                    && stack.BlueprintRuns == incoming.BlueprintRuns
                    && stack.MaterialEfficiency == incoming.MaterialEfficiency
                    && stack.TimeEfficiency == incoming.TimeEfficiency
                    && stack.Quantity > 0
                    && stack.Quantity <= int.MaxValue - ore.Quantity
                    && !depositedIds.Contains(stack.Id)
                    && escrow.Find(stack.Id) is null
                )
                .MinBy(stack => stack.Id);
            if (target is not null && items.Find(target.Id) is { } retained)
            {
                retained.Quantity = InventoryStackRules.Merge(ore, PilotInventoryStore.Read(catalog, retained), ore.Quantity).Target;
                world.Remove(incoming);
            }
        }
    }

    internal static long NextId(WorldAction world) => world.NextItemId();
}
