using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private sealed record DemandLocation(int StationId, int TypeId);

    private int SettleRestingSales(
        WorldDbContext world,
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

    public int AdvanceDemand(int limit = PersonalMarketRules.MaintenanceBatch)
    {
        if (Eden.World.Runtime.LiveWorld.Find(database.Path) is { } live)
        {
            return ResidentAdvanceDemand(live, limit);
        }
        if (limit is < 1 or > PersonalMarketRules.MaintenanceBatch)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }
        var snapshot = activity?.Read();
        if (snapshot is not { Active: true } || snapshot.ElapsedTicks < signal.DemandDeadline)
        {
            return 0;
        }
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        if (!RegionalMarketStore.Enabled(world))
        {
            transaction.Commit();
            signal.ScheduleDemand(long.MaxValue, 0);
            return 0;
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = Now;
        long cursor = signal.DemandCursor;
        var open = world
            .Set<SavedPersonalOrder>()
            .Where(o => !o.Bid && o.Status == PersonalOrderStatus.Open && o.Expires > now && o.Remaining > 0);
        var page = open.Where(o => o.Id > cursor).OrderBy(o => o.Id).Take(limit).ToArray();
        int settled = 0;
        foreach (var location in page.Select(o => new DemandLocation(o.StationId, o.TypeId)).Distinct())
        {
            Seed(world, catalog, Station(catalog, location.StationId), location.TypeId);
            settled += SettleRestingSales(world, catalog, location.StationId, location.TypeId, limit - settled);
            if (settled == limit)
            {
                break;
            }
        }
        bool more = page.Length == limit || settled == limit;
        long next = long.MaxValue;
        if (!more && open.Any())
        {
            long fallback = checked(snapshot.ElapsedTicks + RegionalMarketRules.RecoveryIntervalTicks);
            var boundaries =
                from stock in world.Set<SavedRegionalMarketStock>()
                join bid in world.Set<NpcMarketOrder>() on stock.OrderId equals bid.Id
                where
                    bid.Kind == NpcMarketKind.Regional
                    && bid.Bid
                    && bid.Issued > 0
                    && open.Any(o => o.StationId == bid.StationId && o.TypeId == bid.TypeId)
                select (long?)stock.RefreshedTicks;
            next = Math.Min(fallback, checked((boundaries.Min() ?? snapshot.ElapsedTicks) + RegionalMarketRules.RecoveryIntervalTicks));
        }
        world.SaveChanges();
        transaction.Commit();
        if (settled > 0)
        {
            signal.Changed(demand: false);
        }
        // Failed transactions never advance the cursor or deadline. A full page
        // continues next poll so expensive/unfillable listings cannot starve later IDs.
        signal.ScheduleDemand(more ? 0 : next, more ? page[^1].Id : 0);
        return settled;
    }
}
