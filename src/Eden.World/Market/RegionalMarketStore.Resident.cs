using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal sealed partial class RegionalMarketStore
{
    internal static bool Enabled(WorldAction world) =>
        world.Settings.Economy.ProgressiveMarkets
        ?? world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false };

    internal void Advance(
        WorldAction world,
        CatalogDbContext catalog,
        int region,
        int stationId,
        long now,
        int? typeId = null,
        bool includeDemand = true
    ) => AdvanceTypes(world, catalog, region, stationId, now, typeId is { } selected ? [selected] : null, includeDemand);

    internal void AdvanceTypes(
        WorldAction world,
        CatalogDbContext catalog,
        int region,
        int stationId,
        long now,
        IReadOnlyList<int>? types,
        bool includeDemand = true
    )
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("market.advanceStation");
        long elapsed = world.ActiveTicks;
        var profiles = RegionalMarketCatalog.Read(catalog);
        if (!profiles.StationsById.TryGetValue(stationId, out var profile))
        {
            return;
        }
        var unlocks = world.Set<SavedMarketUnlock>();
        var unlocked = types is null
            ? unlocks.Select(u => u.TypeId).ToHashSet()
            : types.Where(type => unlocks.Find(type) is not null).ToHashSet();
        var products = unlocked
            .Select(type => RegionalMarketCatalog.Product(catalog, type))
            .OfType<RegionalProduct>()
            .Where(p => RegionalMarketRules.Stocks(p.TypeId, p.Category, profile.Role))
            .ToDictionary(p => p.TypeId);
        if (profile.StarterSupply)
        {
            foreach (var starter in profiles.StarterProducts.Values.Where(p => types == null || Enumerable.Contains(types, p.TypeId)))
            {
                products.TryAdd(starter.TypeId, starter);
            }
        }
        var stationOrders = world
            .Set<NpcMarketOrder>()
            .Where(o => o.StationId == stationId && o.Kind == NpcMarketKind.Regional && (includeDemand || !o.Bid));
        if (types is not null)
        {
            stationOrders = stationOrders.Where(o => Enumerable.Contains(types, o.TypeId));
        }
        var orders = stationOrders.ToDictionary(o => (o.TypeId, o.Bid));
        var states = world.Set<SavedRegionalMarketStock>();
        IEnumerable<RegionalDemand> candidates =
            !includeDemand ? []
            : types is not null ? types.Select(type => profiles.DemandFor(catalog, type)).OfType<RegionalDemand>()
            : profiles.Demand.Values;
        var demand = candidates
            .Where(d => !d.RequiresUnlock || Enumerable.Contains(unlocked, d.TypeId))
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
                    profile.Role == RegionalMarketRole.Hub || profile.StartingStation && profiles.StarterProducts.ContainsKey(productType)
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
                state = states.Find(order.Id) ?? throw new InvalidDataException("The regional market order has no stock state.");
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

    internal static void Requote(WorldAction world, NpcMarketOrder order)
    {
        var state = world.Set<SavedRegionalMarketStock>().Find(order.Id)!;
        order.PriceCents = RegionalMarketRules.Quote(state.AnchorCents, order.Bid, order.Remaining, order.Entered);
    }
}
