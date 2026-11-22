using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private int SettleRestingSales(
        WorldAction world,
        CatalogDbContext catalog,
        int stationId,
        int typeId,
        int limit,
        long excludedOrder = 0,
        long maximumAsk = long.MaxValue,
        bool requireComplete = false
    )
    {
        if (!RegionalMarketStore.Enabled(world))
        {
            return 0;
        }
        long now = Now;
        var resident = world as EconomyTransaction;
        IQueryable<NpcMarketOrder>? sqlBids = null;
        SavedPersonalOrder[] listings;
        if (resident is not null)
        {
            long highest = RegionalBids(resident, stationId, typeId).Select(order => order.PriceCents).DefaultIfEmpty().Max();
            long ceiling = Math.Min(maximumAsk, highest);
            // Read the private transaction view before opening only the selected listings for writes.
            var selected = resident
                .StationPersonalOrders(stationId, typeId)
                .Where(order =>
                    !order.Bid
                    && order.Status == PersonalOrderStatus.Open
                    && order.Expires > now
                    && order.Remaining > 0
                    && order.Id != excludedOrder
                    && order.PriceCents <= ceiling
                )
                .OrderBy(order => order.PriceCents)
                .ThenBy(order => order.Modified)
                .ThenBy(order => order.Id)
                .Take(limit + 1)
                .Select(order => order.Id)
                .ToArray();
            var writable = world.Set<SavedPersonalOrder>();
            listings = Array.ConvertAll(selected, id => writable.Find(id)!);
        }
        else
        {
            sqlBids = world
                .Set<NpcMarketOrder>()
                .Where(order =>
                    order.Kind == NpcMarketKind.Regional
                    && order.Bid
                    && order.StationId == stationId
                    && order.TypeId == typeId
                    && order.Remaining > 0
                    && order.Issued > 0
                );
            long highest = sqlBids.Max(order => (long?)order.PriceCents) ?? 0;
            long ceiling = Math.Min(maximumAsk, highest);
            listings = world
                .Set<SavedPersonalOrder>()
                .Where(order =>
                    order.StationId == stationId
                    && order.TypeId == typeId
                    && !order.Bid
                    && order.Status == PersonalOrderStatus.Open
                    && order.Expires > now
                    && order.Remaining > 0
                    && order.Id != excludedOrder
                    && order.PriceCents <= ceiling
                )
                .OrderBy(order => order.PriceCents)
                .ThenBy(order => order.Modified)
                .ThenBy(order => order.Id)
                .Take(limit + 1)
                .ToArray();
        }
        int settled = 0;
        foreach (var order in listings.Take(limit))
        {
            if (order.Status != PersonalOrderStatus.Open || order.Remaining == 0)
            {
                continue;
            }
            List<InventoryChange> changes = [];
            var trades = MatchOrders(world, catalog, order, changes, npcDemandOnly: true);
            if (trades.Count == 0)
            {
                break;
            }
            Event(world, order, MarketOrderChangeKind.Settled);
            PilotTaskTrackingStore.Record(world, catalog, order.PilotId, now, TaskEvents.Sold, trades.Count);
            settled++;
        }
        world.SaveChanges();
        if (requireComplete && listings.Length > limit)
        {
            long nextPrice = listings[limit].PriceCents;
            bool bidsRemain = resident is not null
                ? RegionalBids(resident, stationId, typeId).Any(order => order.PriceCents >= nextPrice)
                : sqlBids!.Any(order => order.PriceCents >= nextPrice);
            if (bidsRemain)
            {
                throw new InvalidDataException("Market demand is settling queued listings. Try again shortly.");
            }
        }
        return settled;
    }

    private static IEnumerable<EconomyNpcMarketOrder> RegionalBids(EconomyTransaction world, int station, int type) =>
        world
            .StationMarketOrders(station, type)
            .Where(order => order.Kind == NpcMarketKind.Regional && order.Bid && order.Remaining > 0 && order.Issued > 0);
}
