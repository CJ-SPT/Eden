using Eden.Data.World.Combat;
using Eden.Data.World.Fittings;
using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

// Transaction boundary for heat burnout and station/module repair owners.
internal static partial class ModuleConditionPersistence
{
    internal static ModuleCondition Read(WorldDbContext world, CatalogDbContext catalog, OwnedItem item) =>
        WorldConnections.ModuleCondition(world, item.OwnerId, item.LocationId, item.Id)
        ?? Read(catalog, item, world.Set<SavedModuleCondition>().Find(item.Id)?.Damage ?? 0);

    internal static ModuleCondition Read(CatalogDbContext catalog, OwnedItem item, double damage)
    {
        if (catalog.RequireItem(item.TypeId).CategoryId != (int)InventoryCategory.Module || !item.Singleton || item.Quantity != 1)
        {
            throw new InvalidDataException("Only an assembled module has module condition.");
        }
        return ModuleConditionRules.Define(
            item.Id,
            catalog.FittingAttributes(item.TypeId).GetValueOrDefault(DogmaAttributeId.StructureHitPoints),
            damage
        );
    }

    internal static IQueryable<double> OwnedDamage(WorldDbContext world, long pilot, long module) =>
        from condition in world.Set<SavedModuleCondition>()
        join item in world.Set<OwnedItem>() on condition.ModuleId equals item.Id
        where condition.ModuleId == module && item.OwnerId == pilot
        select condition.Damage;

    internal static ModuleConditionChange ApplyDamage(WorldDbContext world, CatalogDbContext catalog, long module, double damage, long now)
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Module condition changes require the owning transaction.");
        }
        var item = world.Set<OwnedItem>().Find(module) ?? throw new InvalidDataException("The fitted module is unavailable.");
        if (
            !(
                InventoryDefinitions.IsLowSlot(item.FlagId)
                || InventoryDefinitions.IsMediumSlot(item.FlagId)
                || InventoryDefinitions.IsHighSlot(item.FlagId)
            ) || !world.Ships.Any(s => s.Id == item.LocationId && s.OwnerId == item.OwnerId)
        )
        {
            throw new InvalidDataException("Heat damage applies only to modules fitted to an owned ship rack.");
        }
        var before = Read(world, catalog, item);
        var current = ModuleConditionRules.ApplyDamage(before, damage);
        if (current.Damage > 0)
        {
            var row = world.Set<SavedModuleCondition>().Find(module) ?? world.Add(new SavedModuleCondition { ModuleId = module }).Entity;
            row.Damage = current.Damage;
        }
        bool offline = current.BurnedOut && item.Online;
        if (current.BurnedOut)
        {
            BurnOut(world, catalog, item, now);
        }
        return new(before, current, offline);
    }

    internal static ModuleCondition Repair(WorldDbContext world, CatalogDbContext catalog, OwnedItem item, double amount)
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Module condition changes require the owning transaction.");
        }
        var condition = ModuleConditionRules.Repair(Read(world, catalog, item), amount);
        var saved = world.Set<SavedModuleCondition>().Find(item.Id);
        if (saved is not null)
        {
            if (condition.Damage == 0)
            {
                world.Remove(saved);
            }
            else
            {
                saved.Damage = condition.Damage;
            }
        }
        return condition;
    }

    internal static void Reconcile(WorldDbContext world, CatalogDbContext catalog, long now)
    {
        foreach (var saved in world.Set<SavedModuleCondition>().ToArray())
        {
            var item = world.Set<OwnedItem>().Find(saved.ModuleId) ?? throw new InvalidDataException("Saved module condition has no item.");
            var condition = Read(world, catalog, item);
            if (condition.Damage == 0)
            {
                world.Remove(saved);
            }
            if (condition.BurnedOut)
            {
                BurnOut(world, catalog, item, now);
            }
        }
        world.SaveChanges();
    }

    private static void BurnOut(WorldDbContext world, CatalogDbContext catalog, OwnedItem item, long now)
    {
        // Reconciliation can also encounter already-offline or loose damaged items.
        var ship = item.Online && ShipHeatPersistence.Rack(item.FlagId) is not null ? world.Ships.Find(item.LocationId) : null;
        var pilot = ship is not null && ship.OwnerId == item.OwnerId ? world.Pilots.Find(item.OwnerId) : null;
        var before = pilot is null ? null : PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
        item.Online = false;
        CancelActivity(world, item.Id);
        if (pilot is not null && before is not null)
        {
            var after = PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
            ShipResources.ResizeAfterBurnout(world, item.LocationId, before, after, now);
        }
    }

    private static void CancelActivity(WorldDbContext world, long module)
    {
        world.Set<ActiveFittedModule>().Where(m => m.ModuleId == module).ExecuteDelete();
        world.Set<PendingLocalRepair>().Where(r => r.ModuleId == module).ExecuteDelete();
        world.Set<WorldRemoteRepair>().Where(r => r.ModuleId == module).ExecuteDelete();
        world.Set<WorldRemoteAssistance>().Where(r => r.ModuleId == module).ExecuteDelete();
        world.Set<WorldPlayerEwar>().Where(r => r.ModuleId == module).ExecuteDelete();
        world.Set<WorldTackle>().Where(r => r.ModuleId == module).ExecuteDelete();
    }
}
