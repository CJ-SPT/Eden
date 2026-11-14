using Eden.Data.World.Characters;
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
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal static partial class NaniteRepairPersistence
{
    internal static void RequireAvailable(WorldAction world, CatalogDbContext catalog, long module, long now)
    {
        var tracked = world.ChangeTracker.Entries<SavedNaniteRepair>().FirstOrDefault(e => e.Entity.ModuleId == module);
        if (tracked?.State == EntityState.Deleted)
        {
            return;
        }
        var row = tracked?.Entity ?? world.Set<SavedNaniteRepair>().Find(module);
        if (row is null)
        {
            return;
        }
        if (now >= row.CompletesAt)
        {
            Finish(world, catalog, row, now);
            return;
        }
        throw new InvalidDataException("The module is being repaired with nanite paste.");
    }

    internal static ModuleCondition? Finish(WorldAction world, CatalogDbContext catalog, SavedNaniteRepair row, long now)
    {
        var schedule = Schedule(row);
        var item = world.Set<OwnedItem>().Find(row.ModuleId);
        ModuleCondition? condition = null;
        if (item is { Singleton: true, Quantity: 1 } && catalog.RequireItem(item.TypeId).CategoryId == (int)InventoryCategory.Module)
        {
            condition = ModuleConditionPersistence.Read(world, catalog, item);
            if (!condition.BurnedOut)
            {
                condition = ModuleConditionPersistence.Repair(world, catalog, item, NaniteRepairRules.Repaired(schedule, now));
            }
        }
        long refund = NaniteRepairRules.Refund(schedule, now);
        if (refund > 0)
        {
            RefundPaste(world, schedule, refund);
        }
        world.Remove(row);
        return condition;
    }

    private static void RefundPaste(WorldAction world, NaniteRepairSchedule schedule, long quantity)
    {
        long ship = world.Ships.Any(s => s.Id == schedule.ShipId && s.OwnerId == schedule.PilotId)
            ? schedule.ShipId
            : world.Pilots.Where(p => p.Id == schedule.PilotId).Select(p => p.ActiveShipId).Single();
        var stacks = world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == schedule.PilotId
                && i.LocationId == ship
                && i.FlagId == (int)InventoryFlag.Cargo
                && i.TypeId == NaniteRepairRules.PasteType
                && !i.Singleton
            )
            .OrderBy(i => i.Id)
            .ToArray();
        foreach (var stack in stacks)
        {
            int returned = checked((int)Math.Min(quantity, int.MaxValue - stack.Quantity));
            stack.Quantity += returned;
            quantity -= returned;
        }
        while (quantity > 0)
        {
            int returned = checked((int)Math.Min(quantity, int.MaxValue));
            world.Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = schedule.PilotId,
                    LocationId = ship,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = NaniteRepairRules.PasteType,
                    Quantity = returned,
                }
            );
            quantity -= returned;
        }
    }

    internal static void BeforeShipLoss(WorldAction world, CatalogDbContext catalog, long ship, long now)
    {
        foreach (var row in world.Set<SavedNaniteRepair>().Where(r => r.ShipId == ship).ToArray())
        {
            Finish(world, catalog, row, now);
        }
    }

    internal static bool Active(WorldAction world, long module, long now) =>
        world.Set<ModuleCooldown>().Any(c => c.ModuleId == module && c.Kind == ModuleCooldownKind.Cycle && c.ReadyAt > now)
        || world.Set<ActiveFittedModule>().Any(m => m.ModuleId == module && (m.ExpiresAt == 0 || m.ExpiresAt > now))
        || world.Set<PendingLocalRepair>().Any(r => r.ModuleId == module)
        || world.Set<WorldRemoteRepair>().Any(r => r.ModuleId == module)
        || world.Set<WorldRemoteAssistance>().Any(r => r.ModuleId == module)
        || world.Set<WorldPlayerEwar>().Any(r => r.ModuleId == module)
        || world.Set<WorldTackle>().Any(r => r.ModuleId == module);

    internal static NaniteRepairQuote Quote(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long moduleId,
        FittingCalculation? calculation = null
    )
    {
        var pilot = world.Pilots.Find(pilotId);
        var item = world.Set<OwnedItem>().Find(moduleId);
        if (
            pilot is null
            || item is null
            || item.OwnerId != pilotId
            || item.LocationId != pilot.ActiveShipId
            || ShipHeatPersistence.Rack(item.FlagId) is null
        )
        {
            throw new InvalidDataException("Quote nanite repairs only for a module fitted to your active ship.");
        }
        var condition = ModuleConditionPersistence.Read(world, catalog, item);
        var price = NaniteRepairCatalog.Price(catalog, item.TypeId);
        calculation ??= FittingCalculation.Read(world, catalog, pilot);
        return NaniteRepairRules.Quote(
            condition,
            price.ComputedBasePrice,
            price.PasteBasePrice,
            new(
                calculation.Graph.Read(pilotId, DogmaAttributeId.ModuleRepairRate),
                calculation.Graph.Read(pilotId, DogmaAttributeId.ModuleRepairCostMultiplier)
            )
        );
    }
}
