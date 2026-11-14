using System.Globalization;
using System.Text.Json;
using Eden.Data.Catalog;
using Eden.Data.World.Characters;
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
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Insurance;

[Injectable]
internal sealed partial class PilotInsuranceStore(WorldDatabase database, TimeProvider clock) : IPilotInsurance
{
    private long Now => clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    public long Price(int typeId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => Value(world, catalog, typeId));
    }

    public IReadOnlyDictionary<int, long> Prices(IReadOnlyList<int> typeIds)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect(database, world => typeIds.Distinct().ToDictionary(id => id, id => Value(world, catalog, id)));
    }

    private static long Value(WorldDbContext world, CatalogDbContext catalog, int typeId) =>
        Value(new SqlWorldAction(world), catalog, typeId);

    public IReadOnlyList<InsurancePolicy> Contracts(long pilotId)
    {
        long now = Now;
        return WorldActionScope.Inspect(
            database,
            world =>
                world
                    .Filter<SavedInsurancePolicy>(p =>
                        p.PilotId == pilotId && p.Status == InsurancePolicyStatus.Active && p.StartTime <= now && p.EndTime > now
                    )
                    .OrderBy(p => p.ShipId)
                    .AsEnumerable()
                    .Where(p => world.Ships.Find(p.ShipId)?.OwnerId == pilotId)
                    .Select(p => p.Snapshot())
                    .ToArray()
        );
    }

    public InsurancePolicy? Contract(long pilotId, long shipId) => Contracts(pilotId).SingleOrDefault(policy => policy.ShipId == shipId);

    public InsurancePurchase Insure(long pilotId, long stationId, long shipId, long premiumCents, bool replace)
    {
        using var scope = new WorldActionScope(database, new(pilots: [pilotId], ships: [shipId]));
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        var ship = ShipAtStation(world, catalog, pilotId, stationId, shipId);
        long value = Value(world, catalog, ship.TypeId);
        var level = InsuranceRules.LevelForPremium(value, premiumCents);
        long now = Now;
        var previous = world
            .Filter<SavedInsurancePolicy>(p => p.ShipId == shipId && p.Status == InsurancePolicyStatus.Active)
            .SingleOrDefault();
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
        scope.Commit();
        return new(policy.Snapshot(), balance);
    }

    public void Cancel(long pilotId, long stationId, long shipId)
    {
        using var scope = new WorldActionScope(database, new(pilots: [pilotId], ships: [shipId]));
        var world = scope.World;
        using CatalogDbContext catalog = new(database.CatalogPath);
        ShipAtStation(world, catalog, pilotId, stationId, shipId);
        var policy = world
            .Filter<SavedInsurancePolicy>(p => p.ShipId == shipId && p.PilotId == pilotId && p.Status == InsurancePolicyStatus.Active)
            .SingleOrDefault();
        if (policy is null)
        {
            throw new InvalidDataException("This ship has no active policy to cancel.");
        }
        policy.Status = InsurancePolicyStatus.Cancelled;
        scope.Commit();
    }

    public void CheckStation(long pilotId, long stationId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        WorldActionScope.Inspect(database, world => CheckStation(world, catalog, pilotId, stationId));
    }

    private static void CheckStation(WorldAction world, CatalogDbContext catalog, long pilotId, long stationId)
    {
        var pilot = world.Filter<Pilot>(p => p.Id == pilotId).SingleOrDefault() ?? throw new InvalidDataException("Unknown pilot.");
        if (pilot.ActiveShip.Position is not null || pilot.ActiveShip.DockedLocationId != stationId)
        {
            throw new InvalidDataException("Insurance changes are available only at your current station.");
        }
        if (pilot.ActiveShip.StructureId is long structureId)
        {
            var structure = Eden.World.Structures.PilotStructureStore.Require(world, structureId);
            if (
                !Eden.World.Structures.PilotStructureStore.Access(world, structure, pilotId)
                || !Eden
                    .World.Structures.PilotStructureStore.Snapshot(world, catalog, structure)
                    .Services.Contains(Eden.Gameplay.Structures.StructureService.Insurance)
            )
            {
                throw new InvalidDataException("Insurance access to this structure is denied.");
            }
            return;
        }
        var station = catalog.Definitions.FindStation(stationId) ?? throw new InvalidDataException("Unknown station.");
        if (!StationCapabilities.Has(catalog, station.OperationId, StationService.Insurance))
        {
            throw new InvalidDataException("This station does not provide insurance.");
        }
    }

    private static Ship ShipAtStation(WorldAction world, CatalogDbContext catalog, long pilotId, long stationId, long shipId)
    {
        CheckStation(world, catalog, pilotId, stationId);
        return
            world.Ships.Find(shipId) is { Position: null, StoredInId: null } ship
            && ship.OwnerId == pilotId
            && ship.DockedLocationId == stationId
            ? ship
            : throw new InvalidDataException("Choose an owned assembled ship in your current station hangar.");
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
