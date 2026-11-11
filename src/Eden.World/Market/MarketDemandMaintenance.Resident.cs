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
        EconomyTransaction world,
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
        var bids = world
            .Set<NpcMarketOrder>()
            .Where(o =>
                o.Kind == NpcMarketKind.Regional
                && o.Bid
                && o.StationId == stationId
                && o.TypeId == typeId
                && o.Remaining > 0
                && o.Issued > 0
            );
        long highest = bids.Max(o => (long?)o.PriceCents) ?? 0;
        long ceiling = Math.Min(maximumAsk, highest);
        var listings = world
            .Set<SavedPersonalOrder>()
            .Where(o =>
                o.StationId == stationId
                && o.TypeId == typeId
                && !o.Bid
                && o.Status == PersonalOrderStatus.Open
                && o.Expires > now
                && o.Remaining > 0
                && o.Id != excludedOrder
                && o.PriceCents <= ceiling
            )
            .OrderBy(o => o.PriceCents)
            .ThenBy(o => o.Modified)
            .ThenBy(o => o.Id)
            .Take(limit + 1)
            .ToArray();
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
        if (requireComplete && listings.Length > limit && bids.Any(o => o.PriceCents >= listings[limit].PriceCents))
        {
            throw new InvalidDataException("Market demand is settling queued listings. Try again shortly.");
        }
        return settled;
    }
}
