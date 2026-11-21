using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Information;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    internal MarketOrder? AffordableSalvager(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, int type)
    {
        long budget = (world.Set<PilotWallet>().Find(pilot.PilotId)?.Cents ?? 0) / RattingRules.BudgetDivisor;
        return budget <= 0
            ? null
            : market
                .SupplyOrders(world, catalog, pilot.StationId, [type], pilot.PilotId)
                .Where(order => order.PriceCents <= budget)
                .MinBy(order => order.PriceCents);
    }

    internal long BuySalvager(EconomyTransaction world, CatalogDbContext catalog, DockedPilot pilot, MarketOrder offer) =>
        market
            .ExecuteBuy(world, catalog, pilot.PilotId, pilot.StationId, [new(offer.TypeId, offer.PriceCents, 1)], 0)
            .Changes.Single(change => change.Item.TypeId == offer.TypeId && change.Item.OwnerId == pilot.PilotId)
            .Item.Id;

    internal bool SellRecoveredSalvage(
        WorldAction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IReadOnlyList<InventoryChange> unloaded,
        long now
    ) => SellRecoveredLoot(world, catalog, pilot, unloaded, now, item => Enum.IsDefined((SalvageMaterial)item.TypeId));

    internal bool SellRecoveredExplorationLoot(
        WorldAction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IReadOnlyList<InventoryChange> unloaded,
        long now
    ) => SellRecoveredLoot(world, catalog, pilot, unloaded, now, item => IsMarketSupply(catalog, item.TypeId));

    private bool SellRecoveredLoot(
        WorldAction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        IReadOnlyList<InventoryChange> unloaded,
        long now,
        Func<InventoryItem, bool> marketable
    )
    {
        if (
            world is not EconomyTransaction
            || pilot.InStructure
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
        )
        {
            return false;
        }
        var stockForSale = unloaded
            .Select(change => change.Item)
            .Where(item => item.LocationId == pilot.StationId && !item.Singleton && marketable(item))
            .DistinctBy(item => item.Id)
            .ToArray();
        if (stockForSale.Length == 0)
        {
            return false;
        }
        var bids = LocalProductionBids(world, pilot.PilotId, pilot.CorpId, pilot.StationId, now);
        bool traded = false;
        foreach (var item in stockForSale)
        {
            var stock = world.Set<OwnedItem>().Find(item.Id);
            if (stock is null)
            {
                continue;
            }
            var bid = bids[item.TypeId].FirstOrDefault(order => order.Remaining > 0 && order.EscrowCents >= order.PriceCents);
            if (bid is not null)
            {
                int quantity = (int)Math.Min(Math.Min(stock.Quantity, bid.Remaining), bid.EscrowCents / bid.PriceCents);
                market.ExecuteSell(
                    world,
                    catalog,
                    pilot.PilotId,
                    [new(stock.Id, pilot.StationId, stock.TypeId, bid.PriceCents, quantity)],
                    0
                );
                traded = true;
            }
        }
        return traded;
    }
}
