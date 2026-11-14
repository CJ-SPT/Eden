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

    internal static void BurnOut(WorldAction world, CatalogDbContext catalog, long module, long now)
    {
        var item = world.Set<OwnedItem>().Find(module) ?? throw new InvalidDataException("The fitted module is unavailable.");
        var ship = world.Ships.Find(item.LocationId);
        if (ship is null || ship.OwnerId != item.OwnerId || ShipHeatPersistence.Rack(item.FlagId) is null)
        {
            throw new InvalidDataException("Heat damage applies only to modules fitted to an owned ship rack.");
        }
        var pilot = item.Online ? world.Pilots.Find(item.OwnerId) : null;
        var before = pilot is null ? null : PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
        item.Online = false;
        world.RemoveRange(world.Filter<ActiveFittedModule>(row => row.ModuleId == module).ToArray());
        world.RemoveRange(world.Filter<PendingLocalRepair>(row => row.ModuleId == module).ToArray());
        world.RemoveRange(world.Filter<WorldRemoteRepair>(row => row.ModuleId == module).ToArray());
        world.RemoveRange(world.Filter<WorldRemoteAssistance>(row => row.ModuleId == module).ToArray());
        world.RemoveRange(world.Filter<WorldPlayerEwar>(row => row.ModuleId == module).ToArray());
        world.RemoveRange(world.Filter<WorldTackle>(row => row.ModuleId == module).ToArray());
        if (pilot is not null && before is not null)
        {
            var after = PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
            ShipResources.ResizeAfterBurnout(world, ship.Id, before, after, now);
        }
    }

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
