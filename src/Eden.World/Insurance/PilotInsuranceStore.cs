using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Content;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Economy;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Insurance;

[Injectable]
internal sealed class PilotInsuranceStore(WorldDatabase database, TimeProvider clock) : IPilotInsurance
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public long Price(int typeId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using WorldDbContext world = new(database.Path);
        return Value(world, catalog, typeId);
    }

    public IReadOnlyDictionary<int, long> Prices(IReadOnlyList<int> typeIds)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        using WorldDbContext world = new(database.Path);
        if (EconomicStore.Enabled(world))
        {
            return typeIds.Distinct().ToDictionary(id => id, id => Value(world, catalog, id));
        }
        var definitions = catalog.Definitions;
        return typeIds
            .Distinct()
            .ToDictionary(
                id => id,
                id =>
                    definitions.Items.TryGetValue(id, out var type)
                    && type.CategoryId == (int)InventoryCategory.Ship
                    && type.GroupId != FleetDefinitions.CapsuleGroup
                        ? InsuranceRules.CatalogValue(definitions.Find(CatalogData.Client.Types, (long)id)?.BasePrice)
                        : 0
            );
    }

    private static long Value(WorldDbContext world, CatalogDbContext catalog, int typeId)
    {
        var type = catalog.Definitions.Items.GetValueOrDefault(typeId);
        if (type?.CategoryId != (int)InventoryCategory.Ship || type.GroupId == FleetDefinitions.CapsuleGroup)
        {
            return 0;
        }
        if (EconomicStore.Enabled(world))
        {
            if (type.GroupId == FleetDefinitions.CorvetteGroup)
            {
                // A freely recovered hull must not mint ISK through repeated losses.
                return 0;
            }
            if (BuildContentPolicy.Current.Insurance.MaterialValuedHullTypes.Contains(typeId))
            {
                var blueprints = catalog.Definitions.BlueprintsForProduct(typeId);
                var recipe = blueprints.Length == 1 ? catalog.Definitions.ManufacturingRecipe(blueprints[0]) : null;
                if (recipe?.Materials is not { Length: > 0 })
                {
                    return 0;
                }
                var materials = recipe.Materials.Value.ToDictionary(
                    m => checked((int)m.TypeID!.Value),
                    m => checked((int)m.Quantity!.Value)
                );
                return checked(
                    (long)Math.Round(EconomicRules.Value(materials, EconomicStore.Prices(world)) * 100, 0, MidpointRounding.ToEven)
                );
            }
        }
        return InsuranceRules.CatalogValue(catalog.Definitions.Find(CatalogData.Client.Types, (long)typeId)?.BasePrice);
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
        long value = Value(world, catalog, ship.TypeId);
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
        var station = catalog.Definitions.FindStation(stationId) ?? throw new InvalidDataException("Unknown station.");
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
        long value = covered ? policy!.ValueCents : Value(world, catalog, ship.TypeId);
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
