using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    // These are the three naturally mined grades in build 3662168.
    private static readonly int[] s_rawMercoxit = [11396, 17869, 17870];

    private static int MercoxitPortion(CatalogDbContext catalog, int type) =>
        catalog.Read(CatalogData.Client.Types, (long)type)?.PortionSize is > 0 and <= int.MaxValue and long portion ? (int)portion : 0;

    internal bool? OfferMercoxit(EconomyTransaction world, CatalogDbContext catalog, DockedPilot miner, long now)
    {
        long pilot = miner.PilotId;
        int station = miner.StationId;
        var listing = world
            .Filter<SavedPersonalOrder>(order => order.PilotId == pilot)
            .AsEnumerable()
            .Any(order =>
                !order.Bid
                && order.StationId == station
                && order.Expires > now
                && order.Status == PersonalOrderStatus.Open
                && s_rawMercoxit.Contains(order.TypeId)
            );
        if (listing)
        {
            return false;
        }
        foreach (var stack in Hangar(world, pilot, station).Where(item => s_rawMercoxit.Contains(item.TypeId)))
        {
            int portion = MercoxitPortion(catalog, stack.TypeId);
            if (portion <= 0 || stack.Quantity < portion)
            {
                continue;
            }
            long funds = world.Set<PilotWallet>().Find(pilot)?.Cents ?? 0;
            if (!PilotMarketStore.HasSupplyListingSlot(world, catalog, pilot) || funds < PersonalMarketRules.MinimumBrokerFeeCents)
            {
                return false;
            }
            long price = MercoxitAsk(catalog, stack.TypeId, portion);
            // Starter miners have little cash for a broker. Post at a lower price when they cannot fund the reference ask.
            while (price > 0 && PilotMarketStore.SupplyBrokerFee(world, catalog, pilot, station, price, portion) > funds)
            {
                price /= 2;
            }
            if (price <= 0)
            {
                return false;
            }
            market.ExecuteSell(world, catalog, pilot, [new(stack.Id, station, stack.TypeId, price, portion)], HaulingRules.OrderDays);
            return true;
        }
        return null;
    }

    private static long MercoxitAsk(CatalogDbContext catalog, int type, int portion)
    {
        var price = catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice ?? 0;
        return HaulingRules.OrePrice(price, portion);
    }

    private static FactorySupplyNeed[] MercoxitNeeds(
        CatalogDbContext catalog,
        DockedPilot factory,
        SavedCorporation corporation,
        OwnedItem[] hangar
    )
    {
        var station = catalog.Definitions.Find(CatalogData.Sde.NpcStations, (long)factory.StationId);
        if (
            station is not { ReprocessingEfficiency: > 0 and <= 1, ReprocessingStationsTake: >= 0 and <= 1, OperationID: long operation }
            || !StationCapabilities.Has(catalog, operation, StationService.Reprocessing)
        )
        {
            return [];
        }
        List<FactorySupplyNeed> needs = [];
        foreach (int type in s_rawMercoxit)
        {
            int portion = MercoxitPortion(catalog, type);
            if (portion <= 0)
            {
                continue;
            }
            if (hangar.Where(item => item.TypeId == type && !item.Singleton).Sum(item => (long)item.Quantity) >= portion)
            {
                return [];
            }
            long ceiling = Math.Min(MercoxitAsk(catalog, type, portion), corporation.BalanceCents / portion);
            if (ceiling > 0)
            {
                needs.Add(new(type, portion, portion, portion, ceiling));
            }
        }
        return needs.ToArray();
    }

    private bool BuyLocalMercoxit(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot factory,
        SavedCorporation corporation,
        FactorySupplyNeed[] needs
    )
    {
        var stock = Hangar(world, factory.PilotId, factory.StationId).ToLookup(item => item.TypeId);
        var orders = market
            .SupplyOrders(world, catalog, factory.StationId, needs.Select(need => need.TypeId).ToArray(), factory.PilotId, corporation.Id)
            .ToLookup(order => order.TypeId);
        foreach (var need in needs)
        {
            int missing = (int)Math.Max(0, need.HangarTarget - stock[need.TypeId].Sum(item => (long)item.Quantity));
            if (
                missing > 0
                && QuoteInput(orders[need.TypeId].TakeWhile(order => order.PriceCents <= need.MaximumUnitPriceCents), need.TypeId, missing)
                    is { } purchase
                && purchase.Quantity == missing
            )
            {
                market.BuyCorporationStock(world, catalog, factory.PilotId, factory.StationId, corporation, [purchase]);
                return true;
            }
        }
        return false;
    }
}
