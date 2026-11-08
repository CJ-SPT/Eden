using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

[Injectable]
internal sealed class PilotNaniteRepairStore(WorldDatabase database) : IPilotNaniteRepair
{
    // Eight physical slots in each of the three ordinary racks; rigs cannot repair.
    private const int MaximumModules = 3 * InventoryDefinitions.SlotsPerRack;

    public NaniteRepairBatch Start(long pilotId, long shipId, IReadOnlyList<long> modules, long now)
    {
        CheckClock(now);
        if (modules.Count is 0 or > MaximumModules || modules.Any(m => m <= 0) || modules.Distinct().Count() != modules.Count)
        {
            throw new InvalidDataException("Select distinct fitted modules to repair.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId);
        if (pilot?.ActiveShipId != shipId || pilot.ActiveShip.Position is null || pilot.ActiveShip.OwnerId != pilotId)
        {
            throw new InvalidDataException("Repair your own active ship's fitted modules in space.");
        }
        var heat = world.Set<SavedShipHeat>().Find(shipId);
        if (heat is not null && ShipHeatPersistence.Decode(heat.StateJson).Cycles.Any(c => c.CompletesAt > now))
        {
            throw new InvalidDataException("Stop paid overload cycles before repairing modules.");
        }
        var calculation = FittingCalculation.Read(world, catalog, pilot);
        var stacks = world
            .Set<OwnedItem>()
            .Where(i =>
                i.OwnerId == pilotId
                && i.LocationId == shipId
                && i.FlagId == (int)InventoryFlag.Cargo
                && i.TypeId == NaniteRepairRules.PasteType
                && !i.Singleton
                && i.Quantity > 0
            )
            .OrderBy(i => i.Id)
            .ToArray();
        long available = stacks.Sum(s => (long)s.Quantity);
        Dictionary<long, InventoryItem> previous = [];
        List<NaniteRepairStarted> started = [];
        List<long> missing = [];
        List<long> failed = [];
        foreach (long module in modules)
        {
            NaniteRepairQuote quote;
            NaniteRepairSchedule? schedule;
            try
            {
                var item = world.Set<OwnedItem>().Find(module);
                if (item?.OwnerId != pilotId || item.LocationId != shipId || ShipHeatPersistence.Rack(item.FlagId) is null)
                {
                    throw new InvalidDataException("Repair only your own fitted modules.");
                }
                NaniteRepairPersistence.RequireAvailable(world, catalog, module, now);
                if (NaniteRepairPersistence.Active(world, module, now))
                {
                    throw new InvalidDataException("Stop the module's paid activity before repairing it.");
                }
                quote = NaniteRepairPersistence.Quote(world, catalog, pilotId, module, calculation);
                schedule = NaniteRepairRules.Start(quote, pilotId, shipId, now, Guid.NewGuid());
            }
            catch (Exception error) when (error is InvalidDataException or OverflowException)
            {
                failed.Add(module);
                continue;
            }
            if (quote.PasteUnits > available)
            {
                missing.Add(module);
                continue;
            }
            long required = quote.PasteUnits;
            foreach (var stack in stacks.Where(s => s.Quantity > 0))
            {
                if (required == 0)
                {
                    break;
                }
                previous.TryAdd(stack.Id, PilotInventoryStore.Read(catalog, stack));
                int used = checked((int)Math.Min(required, stack.Quantity));
                stack.Quantity -= used;
                required -= used;
                if (stack.Quantity == 0)
                {
                    world.Remove(stack);
                }
            }
            available -= quote.PasteUnits;
            var condition = quote.Condition;
            if (schedule is null)
            {
                condition = ModuleConditionPersistence.Repair(world, catalog, world.Set<OwnedItem>().Find(module)!, condition.Damage);
            }
            else
            {
                world.Add(
                    new SavedNaniteRepair
                    {
                        ModuleId = module,
                        Generation = schedule.Key.Generation,
                        PilotId = pilotId,
                        ShipId = shipId,
                        StartedAt = schedule.Key.StartedAt,
                        CompletesAt = schedule.Key.CompletesAt,
                        InitialDamage = schedule.InitialDamage,
                        InitialHitPoints = schedule.InitialHitPoints,
                        PasteUnits = schedule.PasteUnits,
                    }
                );
            }
            started.Add(new(module, schedule, condition));
        }
        var inventory = stacks
            .Where(s => previous.ContainsKey(s.Id))
            .Select(s => new InventoryChange(PilotInventoryStore.Read(catalog, s), previous[s.Id]))
            .ToArray();
        world.SaveChanges();
        transaction.Commit();
        return new(started.ToArray(), missing.ToArray(), failed.ToArray(), inventory);
    }

    public ModuleCondition? Stop(long pilotId, long shipId, NaniteRepairKey expected, long now)
    {
        CheckClock(now);
        NaniteRepairRules.ValidateKey(expected);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var item = world.Set<OwnedItem>().Find(expected.ModuleId);
        if (item?.OwnerId != pilotId || item.LocationId != shipId || ShipHeatPersistence.Rack(item.FlagId) is null)
        {
            throw new InvalidDataException("Stop repairs only on your own fitted module.");
        }
        var row = world.Set<SavedNaniteRepair>().Find(expected.ModuleId);
        if (row is not null && (row.PilotId != pilotId || row.ShipId != shipId || NaniteRepairPersistence.Schedule(row).Key != expected))
        {
            return null;
        }
        var condition = row is null
            ? ModuleConditionPersistence.Read(world, catalog, item)
            : NaniteRepairPersistence.Finish(world, catalog, row, now);
        world.SaveChanges();
        transaction.Commit();
        return condition;
    }

    public IReadOnlyList<ModuleCondition> Cancel(long pilotId, long shipId, long now)
    {
        CheckClock(now);
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var rows = world.Set<SavedNaniteRepair>().Where(r => r.PilotId == pilotId && r.ShipId == shipId).ToArray();
        var conditions = rows.Select(r => NaniteRepairPersistence.Finish(world, catalog, r, now)).OfType<ModuleCondition>().ToArray();
        world.SaveChanges();
        transaction.Commit();
        return conditions;
    }

    private static void CheckClock(long now)
    {
        if (now < 0)
        {
            throw new InvalidDataException("Invalid nanite repair clock.");
        }
    }
}
