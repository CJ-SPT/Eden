using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    public IReadOnlyList<MarketOrder> CorporationOrders(long pilotId)
    {
        if (LiveWorld.Find(database.Path) is not { } live)
        {
            return [];
        }
        return live.Economy.Inspect<IReadOnlyList<MarketOrder>>(world =>
        {
            var pilot = Pilot(world, pilotId);
            if (
                world.Set<SavedCorporation>().Find(pilot.CorporationId) is not { ClosedAt: null } corporation
                || !CorporationRoleStore.Has(
                    world,
                    pilotId,
                    corporation,
                    CorporationRole.Accountant | CorporationRole.Trader | CorporationRole.JuniorAccountant
                )
            )
            {
                return [];
            }
            long now = Now;
            return world
                .Filter<SavedPersonalOrder>(order => order.CorporationId == corporation.Id)
                .Where(order => order.Status == PersonalOrderStatus.Open && order.Expires > now)
                .OrderBy(order => order.Id)
                .AsEnumerable()
                .Select(order => order.Snapshot())
                .ToArray();
        });
    }

    internal static long SupplyBrokerFee(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int station,
        long price,
        int quantity
    ) => PersonalMarketRules.BrokerFee(price, quantity, BrokerRate(world, catalog, pilotId, Station(catalog, station)));

    internal static bool HasSupplyListingSlot(WorldAction world, CatalogDbContext catalog, long pilotId) =>
        HasListingSlot(world, catalog, pilotId);

    internal static long EstimatedSupplyProceeds(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int station,
        long price,
        int quantity
    )
    {
        var fill = MarketRules.Fill(quantity, quantity, price, TaxRate(world, catalog, pilotId));
        return fill.GrossCents - fill.TaxCents - SupplyBrokerFee(world, catalog, pilotId, station, price, quantity);
    }

    internal MarketResult BuyCorporationStock(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilotId,
        int station,
        SavedCorporation corporation,
        IReadOnlyList<MarketPurchase> inputs,
        int days = 0,
        List<long>? placedOrders = null
    )
    {
        if (Pilot(world, pilotId).CorporationId != corporation.Id || corporation.ClosedAt is not null)
        {
            throw new InvalidDataException("Corporate purchases require an active employer.");
        }
        return ExecuteBuy(world, catalog, pilotId, station, inputs, days, corporation.Id, placedOrders);
    }

    internal void CancelCorporationBid(EconomyTransaction world, CatalogDbContext catalog, SavedPersonalOrder bid) =>
        Close(world, catalog, bid, PersonalOrderStatus.Cancelled);

    internal void ListCorporationSupply(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long operatorId,
        SavedCorporation corporation,
        OwnedItem stock,
        int quantity,
        long price,
        int days
    )
    {
        var pilot = Pilot(world, operatorId);
        var station = TradeStation(catalog, pilot, pilot.ActiveShip.StationId);
        if (
            pilot.CorporationId != corporation.Id
            || corporation.ClosedAt is not null
            || stock.OwnerId != corporation.Id
            || stock.LocationId != station.Id
            || stock.FlagId != CorporationStorageRules.MarketDeliveriesFlag
            || stock.Singleton
            || quantity <= 0
            || quantity > stock.Quantity
        )
        {
            throw new InvalidDataException("Corporate supply must be listed from owned station deliveries.");
        }
        var order = Incoming(world, catalog, pilot, station, stock.TypeId, false, price, quantity, days, corporation.Id);
        List<InventoryChange> changes = [];
        MatchOrders(world, catalog, order, changes);
        stock.Quantity -= quantity;
        if (stock.Quantity == 0)
        {
            world.Remove(stock);
        }
        Event(world, order, MarketOrderChangeKind.Placed);
    }

    internal MarketTrade BuyCorporationSupply(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long operatorId,
        SavedCorporation corporation,
        SavedPersonalOrder listing,
        int quantity
    )
    {
        var pilot = Pilot(world, operatorId);
        var station = TradeStation(catalog, pilot, listing.StationId);
        if (
            pilot.CorporationId != corporation.Id
            || corporation.ClosedAt is not null
            || listing.CorporationId is null
            || listing.CorporationId == corporation.Id
            || listing.PilotId == operatorId
            || listing.Bid
            || listing.Status != PersonalOrderStatus.Open
            || listing.Expires <= Now
            || quantity > listing.Remaining
        )
        {
            throw new InvalidDataException("The selected corporation supply order is unavailable.");
        }
        var order = Incoming(world, catalog, pilot, station, listing.TypeId, true, listing.PriceCents, quantity, 0, corporation.Id);
        // Keep the listing ID on the market execution and corporate account journal.
        order.Id = listing.Id;
        var trade = MatchOrders(world, catalog, order, [], specificOrder: listing.Id).Single();
        return Read(trade);
    }
}
