using Eden.Data.World.Market;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Economy;
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
        var prices = EconomicStore.Prices(world);
        foreach (int type in profiles.Products.SelectMany(p => p.Materials.Keys).Distinct().Where(t => !prices.ContainsKey(t)))
        {
            var reference = catalog.Read(CatalogData.Client.Types, (long)type)?.BasePrice;
            if (reference is > 0 && double.IsFinite(reference.Value))
            {
                prices[type] = (decimal)reference.Value;
            }
        }
        var orders = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == stationId && o.Kind == NpcMarketKind.Regional)
            .ToDictionary(o => (o.StationId, o.TypeId, o.Bid));
        var states = world
            .Set<SavedRegionalMarketStock>()
            .Where(s => world.Set<NpcMarketOrder>().Any(o => o.Id == s.OrderId && o.StationId == stationId))
            .ToDictionary(s => s.OrderId);
        foreach (var station in profiles.Stations.Where(s => s.Station.Id == stationId))
        {
            foreach (var product in profiles.Products.Where(p => elapsed >= TimeSpan.FromHours(p.UnlockHours).Ticks))
            {
                if (
                    product.Materials.Keys.Any(t => !prices.ContainsKey(t))
                    || product.Category == InventoryCategory.Ship && station.Role != RegionalMarketRole.Hub
                    || product.UnlockHours > 0 && station.Role == RegionalMarketRole.Mining
                )
                {
                    continue;
                }
                decimal value = product.Materials.Sum(m => m.Value * prices[m.Key]) * MarketDefinitions.CentsPerIsk;
                foreach (bool bid in new[] { false, true })
                {
                    if (product.Category == InventoryCategory.Asteroid && !bid)
                    {
                        continue;
                    }
                    long anchor = RegionalMarketRules.Anchor(value, station.Role, product.Category, bid);
                    int capacity = RegionalMarketRules.Capacity(product.Quantity, elapsed);
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
