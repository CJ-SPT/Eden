using System.Globalization;
using System.Text.Json;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Market;
using Eden.Protocol.Repair;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class StationRepairPayment
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public int StationId { get; set; }
    public long StationOwnerId { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }
    public required string RepairsJson { get; set; }
}

[Injectable]
internal sealed class PilotRepairStore(WorldDatabase database) : IPilotRepair
{
    private static Pilot Pilot(WorldDbContext world, long pilotId) =>
        world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);

    private static Station Station(CatalogDbContext catalog, Pilot pilot, int stationId)
    {
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.StationId != stationId)
        {
            throw new InvalidDataException("Repairs are available only at your current station.");
        }
        var station = catalog.Set<Station>().Single(s => s.Id == stationId);
        var operation = catalog
            .Set<SourceRecord>()
            .Find("client/stationoperations", station.OperationId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientStationoperations>();
        if (operation?.Services?.Contains((long)StationService.Repair) != true)
        {
            throw new InvalidDataException("This station does not provide repair facilities.");
        }
        return station;
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        _ = Station(catalog, Pilot(world, pilotId), stationId);
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
                if (ship.OwnerId != pilot.Id || ship.Position is not null || ship.StationId != pilot.ActiveShip.StationId)
                {
                    throw new InvalidDataException("The ship is unavailable in your current station hangar.");
                }
                var type = catalog.Set<ItemType>().Single(t => t.Id == ship.TypeId);
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
                var item = world.Set<OwnedItem>().Find(id);
                var ownerShip = item is null ? null : world.Ships.Include(s => s.Position).SingleOrDefault(s => s.Id == item.LocationId);
                if (
                    item is null
                    || item.OwnerId != pilot.Id
                    || !item.Singleton
                    || !(
                        item.LocationId == pilot.ActiveShip.StationId && item.FlagId == (int)InventoryFlag.Hangar
                        || ownerShip is not null
                            && ownerShip.OwnerId == pilot.Id
                            && ownerShip.Position is null
                            && ownerShip.StationId == pilot.ActiveShip.StationId
                    )
                )
                {
                    throw new InvalidDataException("The assembled item is unavailable in your current station.");
                }
                var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
                if (type.Group.CategoryId != (int)InventoryCategory.Module)
                {
                    throw new InvalidDataException("This item does not support repairs yet.");
                }
                // Eden has no module heat/damage state yet. These owned modules
                // are healthy; do not invent damage or consume them on repair.
                result.Add(
                    id,
                    [
                        new(
                            id,
                            type.Id,
                            type.GroupId,
                            0,
                            catalog.Set<TypeAttribute>().Find(type.Id, (int)DogmaAttributeId.StructureHitPoints)?.Value ?? 0
                        ),
                    ]
                );
            }
        }
        return result;
    }

    public IReadOnlyDictionary<long, IReadOnlyList<RepairQuote>> Quotes(long pilotId, int stationId, IReadOnlyList<long> itemIds)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        _ = Station(catalog, pilot, stationId);
        return Quotes(world, catalog, pilot, itemIds);
    }

    public RepairResult Repair(long pilotId, int stationId, IReadOnlyList<long> itemIds, long paymentCents, long now)
    {
        if (paymentCents < 0)
        {
            throw new InvalidDataException("Repair payments cannot be negative.");
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var pilot = Pilot(world, pilotId);
        var station = Station(catalog, pilot, stationId);
        var quotes = Quotes(world, catalog, pilot, itemIds).Values.SelectMany(q => q).Where(q => q.Damage > 0).ToArray();
        long cost = quotes.Aggregate(
            0L,
            (total, q) =>
                checked(total + checked((long)Math.Ceiling(q.Damage)) * RepairDefinitions.IskPerDamagePoint * MarketDefinitions.CentsPerIsk)
        );
        long charged = Math.Min(paymentCents, cost);
        var wallet = world.Set<PilotWallet>().Find(pilotId);
        long balance = wallet?.Cents ?? 0;
        if (charged > balance)
        {
            throw new InvalidDataException("You do not have enough ISK for this repair payment.");
        }
        if (charged == 0)
        {
            return new(balance, []);
        }
        double fraction = charged / (double)cost;
        List<ShipRepair> repairs = [];
        foreach (var quote in quotes)
        {
            var saved = world.Set<ShipHealthState>().Find(quote.ItemId)!;
            double armor = saved.ArmorDamage * fraction;
            double structure = saved.StructureDamage * fraction;
            saved.ArmorDamage = fraction == 1 ? 0 : saved.ArmorDamage - armor;
            saved.StructureDamage = fraction == 1 ? 0 : saved.StructureDamage - structure;
            // Preserve the shield snapshot and timestamp so payment does not
            // refill shields or restart their natural recharge curve.
            repairs.Add(new(quote.ItemId, armor, structure));
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
                    RepairsJson = JsonSerializer.Serialize(repairs),
                }
            );
        world.SaveChanges();
        transaction.Commit();
        return new(wallet.Cents, repairs);
    }

    public IReadOnlyList<RepairPayment> Payments(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        return world
            .Set<StationRepairPayment>()
            .AsNoTracking()
            .Where(p => p.PilotId == pilotId)
            .OrderByDescending(p => p.Time)
            .ThenByDescending(p => p.Id)
            .Select(p => new RepairPayment(p.Id, p.PilotId, p.StationId, p.StationOwnerId, p.AmountCents, p.BalanceCents, p.Time))
            .ToArray();
    }
}
