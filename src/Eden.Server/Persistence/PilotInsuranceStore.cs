using System.Globalization;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

public sealed class SavedInsurancePolicy
{
    public long Id { get; set; }
    public long ShipId { get; set; }
    public long PilotId { get; set; }
    public int TypeId { get; set; }
    public InsuranceLevel Level { get; set; }
    public long ValueCents { get; set; }
    public long PremiumCents { get; set; }
    public long StartTime { get; set; }
    public long EndTime { get; set; }
    public InsurancePolicyStatus Status { get; set; }

    public InsurancePolicy Snapshot() => new(Id, ShipId, PilotId, TypeId, Level, ValueCents, PremiumCents, StartTime, EndTime);
}

public sealed class SavedInsuranceLedger
{
    public long Id { get; set; }
    public long PilotId { get; set; }
    public long ShipId { get; set; }
    public int TypeId { get; set; }
    public long? PolicyId { get; set; }
    public bool IsPayout { get; set; }
    public long AmountCents { get; set; }
    public long BalanceCents { get; set; }
    public long Time { get; set; }

    public InsuranceLedgerEntry Snapshot() => new(Id, PilotId, ShipId, TypeId, AmountCents, BalanceCents, Time);
}

[Injectable]
internal sealed class PilotInsuranceStore(WorldDatabase database, TimeProvider clock) : IPilotInsurance
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public long Price(int typeId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return Value(catalog, typeId);
    }

    public IReadOnlyDictionary<int, long> Prices(IReadOnlyList<int> typeIds)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        var insurable = catalog
            .Set<ItemType>()
            .Where(type =>
                typeIds.Contains(type.Id)
                && type.Group.CategoryId == (int)InventoryCategory.Ship
                && type.GroupId != FleetDefinitions.CapsuleGroup
            )
            .Select(type => type.Id)
            .ToHashSet();
        var keys = insurable.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToArray();
        var values = catalog
            .Set<SourceRecord>()
            .Where(record => record.Dataset == "client/types" && keys.Contains(record.Key))
            .AsEnumerable()
            .ToDictionary(
                record => int.Parse(record.Key, CultureInfo.InvariantCulture),
                record => InsuranceRules.CatalogValue(record.Read<ClientTypes>().BasePrice)
            );
        return typeIds.Distinct().ToDictionary(id => id, id => values.GetValueOrDefault(id));
    }

    private static long Value(CatalogDbContext catalog, int typeId)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).SingleOrDefault(t => t.Id == typeId);
        if (type?.Group.CategoryId != (int)InventoryCategory.Ship || type.GroupId == FleetDefinitions.CapsuleGroup)
        {
            return 0;
        }
        var source = catalog.Set<SourceRecord>().Find("client/types", typeId.ToString(CultureInfo.InvariantCulture));
        return InsuranceRules.CatalogValue(source?.Read<ClientTypes>().BasePrice);
    }

    public IReadOnlyList<InsurancePolicy> Contracts(long pilotId)
    {
        using WorldDbContext world = new(database.Path);
        long now = Now;
        return world
            .Set<SavedInsurancePolicy>()
            .Where(p =>
                p.PilotId == pilotId
                && p.Status == InsurancePolicyStatus.Active
                && p.StartTime <= now
                && p.EndTime > now
                && world.Ships.Any(s => s.Id == p.ShipId && s.OwnerId == pilotId)
            )
            .OrderBy(p => p.ShipId)
            .AsEnumerable()
            .Select(p => p.Snapshot())
            .ToArray();
    }

    public InsurancePolicy? Contract(long pilotId, long shipId) => Contracts(pilotId).SingleOrDefault(policy => policy.ShipId == shipId);

    public InsurancePurchase Insure(long pilotId, int stationId, long shipId, long premiumCents, bool replace)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = ShipAtStation(world, catalog, pilotId, stationId, shipId);
        long value = Value(catalog, ship.TypeId);
        var level = InsuranceRules.LevelForPremium(value, premiumCents);
        long now = Now;
        var previous = world
            .Set<SavedInsurancePolicy>()
            .SingleOrDefault(p => p.ShipId == shipId && p.Status == InsurancePolicyStatus.Active);
        if (previous is not null && previous.EndTime > now && !replace)
        {
            throw new InsuranceContractExists(previous.PilotId);
        }
        var wallet = world.Set<PilotWallet>().Find(pilotId);
        long balance = InsuranceRules.Pay(wallet?.Cents ?? 0, premiumCents);
        if (previous is not null)
        {
            previous.Status = previous.EndTime > now ? InsurancePolicyStatus.Replaced : InsurancePolicyStatus.Expired;
            // Release the active-policy unique key before inserting its successor.
            // This save and the debit still belong to the same transaction.
            world.SaveChanges();
        }
        var policy = new SavedInsurancePolicy
        {
            ShipId = shipId,
            PilotId = pilotId,
            TypeId = ship.TypeId,
            Level = level,
            ValueCents = value,
            PremiumCents = premiumCents,
            StartTime = now,
            EndTime = checked(now + InsuranceRules.Duration.Ticks),
        };
        world.Add(policy);
        wallet!.Cents = balance;
        world.SaveChanges();
        world.Add(
            new SavedInsuranceLedger
            {
                PilotId = pilotId,
                ShipId = shipId,
                TypeId = ship.TypeId,
                PolicyId = policy.Id,
                AmountCents = -premiumCents,
                BalanceCents = balance,
                Time = now,
            }
        );
        world.SaveChanges();
        transaction.Commit();
        return new(policy.Snapshot(), balance);
    }

    public void Cancel(long pilotId, int stationId, long shipId)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(database.CatalogPath);
        ShipAtStation(world, catalog, pilotId, stationId, shipId);
        var policy = world
            .Set<SavedInsurancePolicy>()
            .SingleOrDefault(p => p.ShipId == shipId && p.PilotId == pilotId && p.Status == InsurancePolicyStatus.Active);
        if (policy is null)
        {
            throw new InvalidDataException("This ship has no active policy to cancel.");
        }
        policy.Status = InsurancePolicyStatus.Cancelled;
        world.SaveChanges();
        transaction.Commit();
    }

    public void CheckStation(long pilotId, int stationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        CheckStation(world, catalog, pilotId, stationId);
    }

    private static void CheckStation(WorldDbContext world, CatalogDbContext catalog, long pilotId, int stationId)
    {
        var pilot =
            world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).SingleOrDefault(p => p.Id == pilotId)
            ?? throw new InvalidDataException("Unknown pilot.");
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.StationId != stationId)
        {
            throw new InvalidDataException("Insurance changes are available only at your current station.");
        }
        var station = catalog.Set<Station>().Find(stationId) ?? throw new InvalidDataException("Unknown station.");
        if (!StationCapabilities.Has(catalog, station.OperationId, StationService.Insurance))
        {
            throw new InvalidDataException("This station does not provide insurance.");
        }
    }

    private static Ship ShipAtStation(WorldDbContext world, CatalogDbContext catalog, long pilotId, int stationId, long shipId)
    {
        CheckStation(world, catalog, pilotId, stationId);
        return world
                .Ships.Include(s => s.Position)
                .SingleOrDefault(s =>
                    s.Id == shipId && s.OwnerId == pilotId && s.StationId == stationId && s.Position == null && s.StoredInId == null
                )
            ?? throw new InvalidDataException("Choose an owned assembled ship in your current station hangar.");
    }

    // The fatal-damage transaction owns this operation. A loss and its wallet
    // credit must commit together; the unique payout key prevents replay.
    internal static InsurancePayout? SettleLoss(WorldDbContext world, CatalogDbContext catalog, Ship ship, long now)
    {
        if (world.Set<SavedInsuranceLedger>().Any(entry => entry.ShipId == ship.Id && entry.IsPayout))
        {
            return null;
        }
        var policy = world
            .Set<SavedInsurancePolicy>()
            .SingleOrDefault(p => p.ShipId == ship.Id && p.Status == InsurancePolicyStatus.Active);
        bool covered =
            policy is not null
            && policy.PilotId == ship.OwnerId
            && policy.TypeId == ship.TypeId
            && policy.StartTime <= now
            && policy.EndTime > now;
        long value = covered ? policy!.ValueCents : Value(catalog, ship.TypeId);
        if (policy is not null)
        {
            policy.Status = covered ? InsurancePolicyStatus.Paid : InsurancePolicyStatus.Expired;
        }
        if (value == 0)
        {
            return null;
        }
        long amount = InsuranceRules.Payout(value, covered ? policy!.Level : InsuranceLevel.Uninsured);
        var wallet = world.Set<PilotWallet>().Find(ship.OwnerId) ?? world.Add(new PilotWallet { PilotId = ship.OwnerId }).Entity;
        wallet.Cents = checked(wallet.Cents + amount);
        world.Add(
            new SavedInsuranceLedger
            {
                PilotId = ship.OwnerId,
                ShipId = ship.Id,
                TypeId = ship.TypeId,
                PolicyId = covered ? policy!.Id : null,
                IsPayout = true,
                AmountCents = amount,
                BalanceCents = wallet.Cents,
                Time = now,
            }
        );
        return new(ship.Id, amount, wallet.Cents);
    }

    internal static void VoidRemovedHull(WorldDbContext world, long shipId)
    {
        var policy = world.Set<SavedInsurancePolicy>().SingleOrDefault(p => p.ShipId == shipId && p.Status == InsurancePolicyStatus.Active);
        if (policy is not null)
        {
            policy.Status = InsurancePolicyStatus.HullRemoved;
        }
    }
}
