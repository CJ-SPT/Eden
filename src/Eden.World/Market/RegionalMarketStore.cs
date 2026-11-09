using Eden.Data.World.Market;
using Eden.Gameplay.Inventory;
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

    internal void Advance(WorldDbContext world, CatalogDbContext catalog, int region, int stationId, long now)
    {
        long elapsed = activity?.Read().ElapsedTicks ?? world.Set<Eden.Data.World.Capsuleers.SavedCapsuleerClock>().Single().ElapsedTicks;
        var profiles = RegionalMarketCatalog.Read(catalog);
        var products = world
            .Set<SavedMarketUnlock>()
            .AsNoTracking()
            .Select(u => u.TypeId)
            .ToArray()
            .Select(type => RegionalMarketCatalog.Product(catalog, type))
            .OfType<RegionalProduct>()
            .ToArray();
        var orders = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == stationId && o.Kind == NpcMarketKind.Regional)
            .ToDictionary(o => (o.StationId, o.TypeId, o.Bid));
        var states = world
            .Set<SavedRegionalMarketStock>()
            .Where(s => world.Set<NpcMarketOrder>().Any(o => o.Id == s.OrderId && o.StationId == stationId))
            .ToDictionary(s => s.OrderId);
        var profile = profiles.Stations.SingleOrDefault(s => s.Station.Id == stationId);
        var stocked = products
            .Where(p => profile is not null && RegionalMarketRules.Stocks(p.TypeId, p.Category, profile.Role))
            .Where(p => MarketSnapshotCatalog.Quotes.ContainsKey(p.TypeId))
            .ToDictionary(p => p.TypeId);
        foreach (
            var retired in orders.Values.Where(o =>
                !stocked.ContainsKey(o.TypeId)
                || (o.Bid ? MarketSnapshotCatalog.Quotes[o.TypeId].BuyCents : MarketSnapshotCatalog.Quotes[o.TypeId].SellCents) is null
            )
        )
        {
            retired.Remaining = 0;
        }
        foreach (var station in profiles.Stations.Where(s => s.Station.Id == stationId))
        {
            foreach (var product in stocked.Values)
            {
                var quote = MarketSnapshotCatalog.Quotes[product.TypeId];
                foreach (bool bid in new[] { false, true })
                {
                    long? reference = bid ? quote.BuyCents : quote.SellCents;
                    if (reference is null)
                    {
                        continue;
                    }
                    long anchor = RegionalMarketRules.SnapshotAnchor(reference.Value, station.Role, bid);
                    int initial =
                        station.Role == RegionalMarketRole.Hub
                            ? product.Quantity
                            : Math.Max(1, product.Quantity / RegionalMarketRules.SecondaryStockDivisor);
                    int capacity = RegionalMarketRules.Capacity(initial, elapsed);
                    var key = (station.Station.Id, product.TypeId, bid);
                    if (!orders.TryGetValue(key, out var order))
                    {
                        order = new NpcMarketOrder
                        {
                            StationId = station.Station.Id,
                            SystemId = station.Station.SolarSystemId,
                            RegionId = region,
                            OwnerId = station.Station.OwnerId,
                            TypeId = product.TypeId,
                            Bid = bid,
                            Kind = NpcMarketKind.Regional,
                            PriceCents = RegionalMarketRules.Quote(anchor, bid, capacity, capacity),
                            Entered = capacity,
                            Remaining = capacity,
                            Issued = now,
                        };
                        world.Add(order);
                        world.SaveChanges();
                        var created = new SavedRegionalMarketStock
                        {
                            OrderId = order.Id,
                            AnchorCents = anchor,
                            RefreshedTicks = elapsed,
                        };
                        world.Add(created);
                        states.Add(order.Id, created);
                        orders.Add(key, order);
                    }
                    var state = states[order.Id];
                    if (order.Issued == 0)
                    {
                        order.Remaining = capacity;
                        state.RefreshedTicks = elapsed;
                    }
                    var recovered = RegionalMarketRules.Recover(
                        new(order.Remaining, order.Entered, state.RefreshedTicks),
                        capacity,
                        elapsed
                    );
                    order.Entered = capacity;
                    order.Remaining = recovered.Remaining;
                    order.Issued = now;
                    state.RefreshedTicks = recovered.RefreshedTicks;
                    state.AnchorCents = anchor;
                    order.PriceCents = RegionalMarketRules.Quote(anchor, bid, order.Remaining, capacity);
                }
            }
        }
        world.SaveChanges();
    }

    internal static void Requote(WorldDbContext world, NpcMarketOrder order)
    {
        var state = world.Set<SavedRegionalMarketStock>().Find(order.Id)!;
        order.PriceCents = RegionalMarketRules.Quote(state.AnchorCents, order.Bid, order.Remaining, order.Entered);
    }
}
