using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed class RegionalMarketStore(CapsuleerActivitySource? activity)
{
    internal static bool Enabled(WorldDbContext world) =>
        world.Settings.Economy.ProgressiveMarkets
        ?? world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false };

    internal void Advance(
        WorldDbContext world,
        CatalogDbContext catalog,
        int region,
        int stationId,
        long now,
        int? typeId = null,
        bool includeDemand = true
    )
    {
        long elapsed = activity?.Read().ElapsedTicks ?? world.Set<Eden.Data.World.Capsuleers.SavedCapsuleerClock>().Single().ElapsedTicks;
        var profiles = RegionalMarketCatalog.Read(catalog);
        var profile = profiles.Stations.SingleOrDefault(s => s.Station.Id == stationId);
        if (profile is null)
        {
            return;
        }
        var unlocked = world
            .Set<SavedMarketUnlock>()
            .AsNoTracking()
            .Where(u => !typeId.HasValue || u.TypeId == typeId.Value)
            .Select(u => u.TypeId)
            .ToHashSet();
        var products = unlocked
            .Select(type => RegionalMarketCatalog.Product(catalog, type))
            .OfType<RegionalProduct>()
            .Where(p => RegionalMarketRules.Stocks(p.TypeId, p.Category, profile.Role))
            .ToDictionary(p => p.TypeId);
        var orders = world
            .Set<NpcMarketOrder>()
            .Where(o =>
                o.StationId == stationId
                && o.Kind == NpcMarketKind.Regional
                && (includeDemand || !o.Bid)
                && (!typeId.HasValue || o.TypeId == typeId.Value)
            )
            .ToDictionary(o => (o.TypeId, o.Bid));
        var states = world
            .Set<SavedRegionalMarketStock>()
            .Where(s =>
                world
                    .Set<NpcMarketOrder>()
                    .Any(o =>
                        o.Id == s.OrderId
                        && o.StationId == stationId
                        && (includeDemand || !o.Bid)
                        && (!typeId.HasValue || o.TypeId == typeId.Value)
                    )
            )
            .ToDictionary(s => s.OrderId);
        IEnumerable<RegionalDemand> candidates =
            !includeDemand ? []
            : typeId.HasValue
                ? profiles.Demand.TryGetValue(typeId.Value, out var selected) ? [selected]
                    : []
            : profiles.Demand.Values;
        var demand = candidates
            .Where(d => !d.RequiresUnlock || unlocked.Contains(d.TypeId))
            .Where(d => RegionalMarketRules.Buys(d.TypeId, d.Category, d.RawResource, d.Material, profile.Role))
            .ToDictionary(d => d.TypeId);
        foreach (
            var retired in orders.Values.Where(o =>
                !(o.Bid ? demand.ContainsKey(o.TypeId) : products.ContainsKey(o.TypeId))
                || !MarketSnapshotCatalog.Quotes.TryGetValue(o.TypeId, out var quote)
                || (o.Bid ? quote.BuyCents : quote.SellCents) is null
            )
        )
        {
            retired.Remaining = 0;
            retired.Issued = 0;
        }
        List<(NpcMarketOrder Order, SavedRegionalMarketStock State)> createdOrders = [];
        foreach (var (productType, bid) in products.Keys.Select(id => (id, false)).Concat(demand.Keys.Select(id => (id, true))))
        {
            var quote = MarketSnapshotCatalog.Quotes.GetValueOrDefault(productType);
            long? reference = bid ? quote?.BuyCents : quote?.SellCents;
            if (reference is null)
            {
                continue;
            }
            long anchor = RegionalMarketRules.SnapshotAnchor(reference.Value, profile.Role, bid);
            int capacity;
            if (bid)
            {
                capacity = RegionalMarketRules.DemandCapacity(reference.Value, profile.Role, elapsed);
            }
            else
            {
                int initial =
                    profile.Role == RegionalMarketRole.Hub
                        ? products[productType].Quantity
                        : Math.Max(1, products[productType].Quantity / RegionalMarketRules.SecondaryStockDivisor);
                capacity = RegionalMarketRules.Capacity(initial, elapsed);
            }
            var key = (productType, bid);
            SavedRegionalMarketStock state;
            if (!orders.TryGetValue(key, out var order))
            {
                order = new NpcMarketOrder
                {
                    StationId = stationId,
                    SystemId = profile.Station.SolarSystemId,
                    RegionId = region,
                    OwnerId = profile.Station.OwnerId,
                    TypeId = productType,
                    Bid = bid,
                    Kind = NpcMarketKind.Regional,
                    PriceCents = anchor,
                    Entered = capacity,
                    Remaining = capacity,
                    Issued = now,
                };
                world.Add(order);
                state = new SavedRegionalMarketStock
                {
                    AnchorCents = anchor,
                    RefreshedTicks = elapsed,
                    DemandVersion = 1,
                };
                createdOrders.Add((order, state));
                orders.Add(key, order);
            }
            else
            {
                state = states[order.Id];
            }
            if (order.Issued == 0)
            {
                order.Remaining = capacity;
                state.RefreshedTicks = elapsed;
            }
            else if (bid && state.DemandVersion == 0)
            {
                // Upgrade demand once, preserving the depleted fraction and its recovery boundary.
                order.Remaining = (int)((long)capacity * order.Remaining / Math.Max(1, order.Entered));
            }
            var recovered = RegionalMarketRules.Recover(new(order.Remaining, order.Entered, state.RefreshedTicks), capacity, elapsed, bid);
            order.Entered = capacity;
            order.Remaining = recovered.Remaining;
            if (order.Issued == 0 || now - order.Issued >= TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks)
            {
                order.Issued = now;
            }
            state.RefreshedTicks = recovered.RefreshedTicks;
            state.AnchorCents = anchor;
            state.DemandVersion = 1;
            order.PriceCents = RegionalMarketRules.Quote(anchor, bid, order.Remaining, capacity);
        }
        world.SaveChanges();
        foreach (var (order, state) in createdOrders)
        {
            state.OrderId = order.Id;
            world.Add(state);
        }
        world.SaveChanges();
    }

    internal static void Requote(WorldDbContext world, NpcMarketOrder order)
    {
        var state = world.Set<SavedRegionalMarketStock>().Find(order.Id)!;
        order.PriceCents = RegionalMarketRules.Quote(state.AnchorCents, order.Bid, order.Remaining, order.Entered);
    }
}
