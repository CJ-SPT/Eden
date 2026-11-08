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
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

// Atomic paid repair settlement, including unused material reservations.
internal static class NaniteRepairPersistence
{
    internal static NaniteRepairSchedule Schedule(SavedNaniteRepair row)
    {
        NaniteRepairSchedule schedule = new(
            new(row.ModuleId, row.StartedAt, row.CompletesAt, row.Generation),
            row.PilotId,
            row.ShipId,
            row.InitialDamage,
            row.InitialHitPoints,
            row.PasteUnits
        )
        {
            PasteUnitsPerDamage = row.PasteUnitsPerDamage,
        };
        NaniteRepairRules.Validate(schedule);
        return schedule;
    }

    internal static ModuleCondition? Finish(WorldDbContext world, CatalogDbContext catalog, SavedNaniteRepair row, long now)
    {
        if (world.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Settle a paid repair inside its owning transaction.");
        }
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

    private static void RefundPaste(WorldDbContext world, NaniteRepairSchedule schedule, long quantity)
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

    internal static void Reconcile(WorldDbContext world, CatalogDbContext catalog, TimeProvider clock)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        foreach (var row in world.Set<SavedNaniteRepair>().ToArray())
        {
            // An interrupted connection cannot restore the client's local
            // repair timer. Settle its paid elapsed work once, then stop it.
            Finish(world, catalog, row, now);
        }
        world.SaveChanges();
    }

    internal static void BeforeShipLoss(WorldDbContext world, CatalogDbContext catalog, long ship, long now)
    {
        foreach (var row in world.Set<SavedNaniteRepair>().Where(r => r.ShipId == ship).ToArray())
        {
            Finish(world, catalog, row, now);
        }
    }

    internal static void RequireAvailable(WorldDbContext world, CatalogDbContext catalog, long module, long now)
    {
        var tracked = world.ChangeTracker.Entries<SavedNaniteRepair>().FirstOrDefault(e => e.Entity.ModuleId == module);
        if (tracked?.State == EntityState.Deleted)
        {
            return;
        }
        if (tracked is null && WorldConnections.TryNaniteRepairing(world, module, now, out bool repairing) && !repairing)
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

    internal static bool Active(WorldDbContext world, long module, long now) =>
        world.Set<ModuleCooldown>().Any(c => c.ModuleId == module && c.Kind == ModuleCooldownKind.Cycle && c.ReadyAt > now)
        || world.Set<ActiveFittedModule>().Any(m => m.ModuleId == module && (m.ExpiresAt == 0 || m.ExpiresAt > now))
        || world.Set<PendingLocalRepair>().Any(r => r.ModuleId == module)
        || world.Set<WorldRemoteRepair>().Any(r => r.ModuleId == module)
        || world.Set<WorldRemoteAssistance>().Any(r => r.ModuleId == module)
        || world.Set<WorldPlayerEwar>().Any(r => r.ModuleId == module)
        || world.Set<WorldTackle>().Any(r => r.ModuleId == module);

    internal static NaniteRepairQuote Quote(
        WorldDbContext world,
        CatalogDbContext catalog,
        long pilotId,
        long moduleId,
        FittingCalculation? calculation = null
    )
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).SingleOrDefault(p => p.Id == pilotId);
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
