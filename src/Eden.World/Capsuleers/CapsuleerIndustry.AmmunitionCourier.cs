using Eden.Data.World.Capsuleers;
using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Rules = Eden.Gameplay.Capsuleers.CapsuleerAmmunitionSupplyRules;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal sealed record Collection(CorporationSupplyRun Run, CapsuleerTrafficRoute Route);

    internal static Collection? PlanCollection(
        EconomyTransaction world,
        CatalogDbContext catalog,
        LiveWorld live,
        DockedPilot courier,
        IReadOnlySet<int> avoided,
        long now
    )
    {
        if (
            courier.ShipGroupId != (int)HullClass.Hauler
            || Employer(world, courier.PilotId, courier.CorpId) is not { } employer
            || courier.StationId != employer.StationId
            || world.Filter<IndustryJob>(job => job.OutputCorporationId == employer.Id).Any(job => job.ProductTypeId == Rules.ChargeType)
            || DepotStock(world, employer.Id, employer.StationId).Sum(stack => stack.Quantity) >= Rules.DepotReorderQuantity
        )
        {
            return null;
        }
        var members = world.Filter<Pilot>(pilot => pilot.CorporationId == employer.Id).OrderBy(pilot => pilot.Id).ToArray();
        if (
            members.Any(pilot => ActiveSupplyRun(live, pilot) is not null)
            || !members.Any(pilot =>
                pilot.AccountId == CapsuleerCommunityStore.MinerAccountId
                && world.Set<SavedCapsuleerSuspension>().Find(pilot.Id) is null
                && UsesCharge(world, catalog, pilot)
            )
        )
        {
            return null;
        }
        long available = employer.BalanceCents + (world.Set<PilotWallet>().Find(courier.PilotId)?.Cents ?? 0) / RattingRules.BudgetDivisor;
        var local = NearbySystems(catalog, courier.SystemId, Rules.MaximumSupplierJumps);
        var offers = world
            .Filter<SavedPersonalOrder>(row => row.TypeId == Rules.ChargeType)
            .Where(row =>
                row.CorporationId.HasValue
                && row.CorporationId != employer.Id
                && row.StationId != courier.StationId
                && !row.Bid
                && row.Remaining > 0
                && row.Status == PersonalOrderStatus.Open
                && row.Expires > now
                && row.PriceCents <= available
                && local.Contains(row.SystemId)
            )
            .OrderBy(row => row.PriceCents)
            .ThenBy(row => row.Id)
            .AsEnumerable();
        foreach (var offer in offers)
        {
            var route = CapsuleerTrafficRoutes.ToStation(
                catalog,
                courier.StationId,
                offer.StationId,
                HullClass.Hauler,
                avoided,
                Rules.MaximumSupplierJumps
            );
            if (route is not null && route.Systems.Length > 1)
            {
                return new(new(employer.Id, offer.Id), route);
            }
        }
        return null;
    }

    private static bool UsesCharge(WorldAction world, CatalogDbContext catalog, Pilot pilot) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == pilot.ActiveShipId)
            .AsEnumerable()
            .Any(module =>
                module.OwnerId == pilot.Id
                && module.Online
                && InventoryDefinitions.IsHighSlot(module.FlagId)
                && CanUseIron(catalog, module.TypeId)
                && world.Filter<OwnedItem>(item => item.LocationId == module.Id).All(item => item.TypeId == Rules.ChargeType)
            );

    internal FreightManifest? Purchase(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        CorporationSupplyRun run,
        int home,
        long now
    )
    {
        if (
            Employer(world, courier.PilotId, run.CorporationId) is not { } buyer
            || world.Set<SavedPersonalOrder>().Find(run.OrderId) is not { Status: PersonalOrderStatus.Open, Bid: false } listing
            || listing.StationId != courier.StationId
            || listing.TypeId != Rules.ChargeType
            || listing.CorporationId is null
            || listing.CorporationId == buyer.Id
            || listing.PilotId == courier.PilotId
            || listing.Expires <= now
        )
        {
            return null;
        }
        double volume = PilotInventoryStore.Volume(catalog, Rules.ChargeType);
        double free = FreeFreightVolume(world, catalog, courier);
        if (volume <= 0)
        {
            return null;
        }
        long money = buyer.BalanceCents + (world.Set<PilotWallet>().Find(courier.PilotId)?.Cents ?? 0) / RattingRules.BudgetDivisor;
        int units = (int)
            Math.Min(
                Math.Min(Rules.ShipmentQuantity, listing.Remaining),
                Math.Min(money / listing.PriceCents, Math.Floor(Math.Max(0, free) / volume))
            );
        if (units <= 0 || !Fund(world, buyer, courier.PilotId, units * listing.PriceCents, now))
        {
            return null;
        }
        var purchase = market.BuyCorporationSupply(world, catalog, courier.PilotId, buyer, listing, units);
        var cargo = StowPurchased(world, courier, purchase.TypeId, purchase.Quantity);
        return new(run.OrderId, home, [cargo]);
    }

    private static double FreeFreightVolume(WorldAction world, CatalogDbContext catalog, DockedPilot courier) =>
        PilotInventoryStore.HoldCapacity(world, catalog, courier.PilotId, courier.ShipId, (int)InventoryFlag.Cargo)
        - InventoryPersistence.UsedVolume(world, catalog, courier.PilotId, courier.ShipId, (int)InventoryFlag.Cargo);

    private static FreightStack StowPurchased(EconomyTransaction world, DockedPilot courier, int type, int quantity)
    {
        // Move only the newly purchased units. Keep them in a distinct ordinary ship cargo stack for loss and checkpoint handling.
        var stocks = Hangar(world, courier.PilotId, courier.StationId).Where(item => item.TypeId == type && !item.Singleton);
        int remaining = quantity;
        foreach (var stock in stocks)
        {
            int moved = Math.Min(stock.Quantity, remaining);
            stock.Quantity -= moved;
            remaining -= moved;
            if (stock.Quantity == 0)
            {
                world.Remove(stock);
            }
            if (remaining == 0)
            {
                break;
            }
        }
        if (remaining != 0)
        {
            throw new InvalidDataException("Purchased cargo is no longer in the courier's hangar.");
        }
        var cargo = world
            .Add(
                new OwnedItem
                {
                    Id = world.NextItemId(),
                    OwnerId = courier.PilotId,
                    LocationId = courier.ShipId,
                    FlagId = (int)InventoryFlag.Cargo,
                    TypeId = type,
                    Quantity = quantity,
                }
            )
            .Entity;
        return new(cargo.Id, cargo.TypeId, quantity);
    }

    internal static void Deliver(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot courier,
        CorporationSupplyRun run,
        FreightManifest freight,
        bool personalWarehouse = false
    )
    {
        if (freight.Destination != courier.StationId)
        {
            return;
        }
        long owner = run.CorporationId;
        int flag = CorporationStorageRules.MarketDeliveriesFlag;
        if (run.FactorySupply is { } factory)
        {
            if (
                freight.Destination != factory.FactoryStationId
                || !personalWarehouse && world.Set<SavedCorporation>().Find(run.CorporationId) is not { ClosedAt: null }
            )
            {
                return;
            }
            if (Employer(world, factory.ManufacturerId, run.CorporationId) is not null)
            {
                owner = factory.ManufacturerId;
                flag = (int)InventoryFlag.Hangar;
            }
        }
        if (personalWarehouse)
        {
            owner = courier.PilotId;
            flag = (int)InventoryFlag.Hangar;
        }
        foreach (var stack in freight.Stacks)
        {
            if (
                world.Set<OwnedItem>().Find(stack.ItemId) is not { } item
                || item.OwnerId != courier.PilotId
                || item.LocationId != courier.ShipId
                || item.FlagId != (int)InventoryFlag.Cargo
                || item.TypeId != stack.TypeId
            )
            {
                continue;
            }
            int delivered = checked((int)Math.Min(item.Quantity, stack.Quantity));
            InventoryPersistence.MergeOutput(world, catalog, owner, courier.StationId, flag, item.TypeId, delivered);
            item.Quantity -= delivered;
            if (item.Quantity == 0)
            {
                world.Remove(item);
            }
        }
    }

    internal static FreightStack? IssueReserve(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int corporationId,
        int station,
        int type,
        int quantity
    )
    {
        if (Employer(world, pilotId, corporationId) is null || quantity <= 0)
        {
            return null;
        }
        var stock = DepotStacks(world, corporationId, station).Where(item => item.TypeId == type).MinBy(item => item.Id);
        if (stock is null)
        {
            return null;
        }
        int units = (int)Math.Min(quantity, stock.Quantity);
        stock.Quantity -= units;
        if (stock.Quantity == 0)
        {
            world.Remove(stock);
        }
        var moved = InventoryPersistence.MergeOutput(world, catalog, pilotId, station, (int)InventoryFlag.Hangar, type, units);
        return new(moved.Item.Id, type, units);
    }
}
