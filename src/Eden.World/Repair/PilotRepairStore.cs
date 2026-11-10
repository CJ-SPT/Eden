using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Repair;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Thermal;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Storage;
using Eden.World.Thermal;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

internal sealed record RepairFacility(long OwnerId);

[Injectable]
internal sealed class PilotRepairStore(WorldDatabase database) : IPilotRepair
{
    private static Pilot Pilot(WorldDbContext world, long pilotId) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);

    private static RepairFacility Station(WorldDbContext world, CatalogDbContext catalog, Pilot pilot, long stationId)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != stationId)
        {
            throw new InvalidDataException("Repairs are available only at your current station.");
        }
        if (pilot.ActiveShip.StructureId is long structureId)
        {
            var structure = Eden.World.Structures.PilotStructureStore.Require(world, structureId);
            if (!structure.HasCore || !Eden.World.Structures.PilotStructureStore.Access(world, structure, pilot.Id))
            {
                throw new InvalidDataException("The structure requires a quantum core and repair access.");
            }
            return new(structure.CorporationId);
        }
        var station = catalog.RequireStation(checked((int)stationId));
        if (!StationCapabilities.Has(catalog, station.OperationId, StationService.Repair))
        {
            throw new InvalidDataException("This station does not provide repair facilities.");
        }
        return new(station.OwnerId);
    }

    public void CheckStation(long pilotId, long stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = Station(world, catalog, Pilot(world, pilotId), stationId);
    }

    private static Dictionary<long, IReadOnlyList<RepairQuote>> Quotes(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        IReadOnlyList<long> ids
    )
    {
        if (ids.Count == 0 || ids.Distinct().Count() != ids.Count)
        {
            throw new InvalidDataException("Select distinct assembled ships or modules to repair.");
        }
        Dictionary<long, IReadOnlyList<RepairQuote>> result = [];
        foreach (long id in ids)
        {
            if (world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == id) is { } ship)
            {
                if (ship.OwnerId != pilot.Id || ship.Position is not null || ship.DockedLocationId != pilot.ActiveShip.DockedLocationId)
                {
                    throw new InvalidDataException("The ship is unavailable in your current station hangar.");
                }
                var type = catalog.RequireItem(ship.TypeId);
                var defense = ShipHealth.DefenseFrom(PilotFittingStore.ShipAttributes(world, catalog, pilot, ship));
                var saved = world.Set<ShipHealthState>().Find(id);
                if (
                    saved is not null
                    && (
                        !double.IsFinite(saved.ArmorDamage)
                        || !double.IsFinite(saved.StructureDamage)
                        || saved.ArmorDamage < 0
                        || saved.StructureDamage < 0
                    )
                )
                {
                    throw new InvalidDataException("Invalid saved ship damage.");
                }
                double damage = (saved?.ArmorDamage ?? 0) + (saved?.StructureDamage ?? 0);
                if (!double.IsFinite(damage) || damage < 0)
                {
                    throw new InvalidDataException("Invalid saved ship damage.");
                }
                result.Add(id, [new(id, type.Id, type.GroupId, damage, defense.Armor + defense.Structure)]);
            }
            else
            {
                var item = InventoryPersistence.OwnedStack(world, pilot.Id, id);
                var ownerShip = item is null ? null : world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == item.LocationId);
                if (
                    item is null
                    || item.OwnerId != pilot.Id
                    || !item.Singleton
                    || item.Quantity != 1
                    || !(
                        item.LocationId == pilot.ActiveShip.DockedLocationId && item.FlagId == (int)InventoryFlag.Hangar
                        || ownerShip is not null
                            && ownerShip.OwnerId == pilot.Id
                            && ownerShip.Position is null
                            && ownerShip.DockedLocationId == pilot.ActiveShip.DockedLocationId
                    )
                )
                {
                    throw new InvalidDataException("The assembled item is unavailable in your current station.");
                }
                var type = catalog.RequireItem(item.TypeId);
                if (type.CategoryId != (int)InventoryCategory.Module)
                {
                    throw new InvalidDataException("This item does not support repairs yet.");
                }
                double damage = world.Set<SavedModuleCondition>().Find(id)?.Damage ?? 0;
                double hp = catalog.FittingAttributes(type.Id).GetValueOrDefault(DogmaAttributeId.StructureHitPoints, 0);
                if (damage != 0)
                {
                    _ = ModuleConditionRules.Define(id, hp, damage);
                }
                result.Add(id, [new(id, type.Id, type.GroupId, damage, hp)]);
            }
        }
        return result;
    }

    public IReadOnlyDictionary<long, IReadOnlyList<RepairQuote>> Quotes(long pilotId, long stationId, IReadOnlyList<long> itemIds)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        _ = Station(world, catalog, pilot, stationId);
        return Quotes(world, catalog, pilot, itemIds);
    }

    public RepairResult Repair(long pilotId, long stationId, IReadOnlyList<long> itemIds, long paymentCents, long now)
    {
        if (paymentCents < 0)
        {
            throw new InvalidDataException("Repair payments cannot be negative.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = Station(world, catalog, pilot, stationId);
        var quotes = Quotes(world, catalog, pilot, itemIds).Values.SelectMany(q => q).Where(q => q.Damage > 0).ToArray();
        bool free = pilot.ActiveShip.StructureId.HasValue;
        var budget = free ? new RepairBudget(0, 1) : RepairRules.Allocate(quotes, paymentCents);
        long charged = budget.ChargedCents;
        var wallet = world.Set<PilotWallet>().Find(pilotId);
        long balance = wallet?.Cents ?? 0;
        if (charged > balance)
        {
            throw new InvalidDataException("You do not have enough ISK for this repair payment.");
        }
        if (charged == 0 && !free)
        {
            return new(balance, [], []);
        }
        double fraction = budget.Fraction;
        List<ShipRepair> repairs = [];
        List<ModuleRepair> modules = [];
        foreach (var quote in quotes)
        {
            if (world.Set<OwnedItem>().Find(quote.ItemId) is { } module)
            {
                double repaired = quote.Damage * fraction;
                var condition = ModuleConditionPersistence.Repair(world, catalog, module, repaired);
                modules.Add(new(module.Id, module.TypeId, quote.Damage - condition.Damage, condition.Damage));
                continue;
            }
            var saved = world.Set<ShipHealthState>().Find(quote.ItemId)!;
            var repair = RepairRules.Apply(quote.ItemId, saved.ArmorDamage, saved.StructureDamage, budget);
            double armor = repair.ArmorRepaired;
            double structure = repair.StructureRepaired;
            saved.ArmorDamage = fraction == 1 ? 0 : saved.ArmorDamage - armor;
            saved.StructureDamage = fraction == 1 ? 0 : saved.StructureDamage - structure;
            // Preserve the shield snapshot and timestamp so payment does not
            // refill shields or restart their natural recharge curve.
            repairs.Add(repair);
        }
        if (free)
        {
            world.SaveChanges();
            transaction.Commit();
            return new(balance, repairs, modules);
        }
        wallet!.Cents = balance - charged;
        world
            .Set<StationRepairPayment>()
            .Add(
                new()
                {
                    PilotId = pilotId,
                    StationId = stationId,
                    StationOwnerId = station.OwnerId,
                    AmountCents = charged,
                    BalanceCents = wallet.Cents,
                    Time = now,
                    // Keep historical ship-only receipts in their existing array
                    // shape; module/mixed settlements use named detail lists.
                    RepairsJson =
                        modules.Count == 0
                            ? JsonSerializer.Serialize(repairs)
                            : JsonSerializer.Serialize(new StationRepairDetails(repairs, modules)),
                }
            );
        world.SaveChanges();
        transaction.Commit();
        return new(wallet.Cents, repairs, modules);
    }

    public IReadOnlyList<RepairPayment> Payments(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return Payments(world, pilotId);
    }

    internal static IReadOnlyList<RepairPayment> Payments(WorldDbContext world, long pilotId, HistoryPeriod? period = null)
    {
        long from = period?.From ?? 0;
        long until = period?.Until ?? long.MaxValue;
        return world
            .Set<StationRepairPayment>()
            .AsNoTracking()
            .Where(p => p.PilotId == pilotId && p.Time >= from && p.Time < until)
            .OrderByDescending(p => p.Time)
            .ThenByDescending(p => p.Id)
            .Select(p => new RepairPayment(p.Id, p.PilotId, p.StationId, p.StationOwnerId, p.AmountCents, p.BalanceCents, p.Time))
            .ToArray();
    }
}
