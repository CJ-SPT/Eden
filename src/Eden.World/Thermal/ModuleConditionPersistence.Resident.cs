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
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal static partial class ModuleConditionPersistence
{
    internal static ModuleCondition Read(WorldAction world, CatalogDbContext catalog, OwnedItem item) =>
        Read(catalog, item, world.Set<SavedModuleCondition>().Find(item.Id)?.Damage ?? 0);

    internal static IQueryable<double> OwnedDamage(WorldAction world, long pilot, long module) =>
        world
            .Set<SavedModuleCondition>()
            .Where(condition =>
                condition.ModuleId == module && world.Set<OwnedItem>().Any(item => item.Id == module && item.OwnerId == pilot)
            )
            .Select(condition => condition.Damage);

    internal static ModuleCondition Repair(WorldAction world, CatalogDbContext catalog, OwnedItem item, double amount)
    {
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
}
