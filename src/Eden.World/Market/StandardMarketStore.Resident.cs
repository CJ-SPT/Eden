using Eden.Data.Catalog.Market;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Market;

internal sealed partial class PilotMarketStore
{
    private void RequireResidentStandardMarket()
    {
        if (database.Settings.Economy.StandardMarket && Eden.World.Runtime.LiveWorld.Find(database.Path) is null)
        {
            throw new InvalidOperationException("Standard market operations require the resident world owner.");
        }
    }

    private static bool StandardBidReaches(CatalogDbContext catalog, NpcMarketOrder order, SavedPersonalOrder incoming)
    {
        return !order.Bid || BuyReaches(catalog, order.Range, order.StationId, order.SystemId, incoming.StationId, incoming.SystemId);
    }

    private static bool BuyReaches(CatalogDbContext catalog, int range, int buyStation, int buySystem, int sellStation, int sellSystem)
    {
        if (buyStation == sellStation || range == StandardMarketRules.RegionRange)
        {
            return true;
        }
        if (range == MarketDefinitions.StationRange)
        {
            return false;
        }
        return buySystem == sellSystem
            || range > 0 && StandardMarketRules.Reaches(range, false, catalog.Definitions.StargateDistance(sellSystem, buySystem));
    }

    private static EconomyTransaction ResidentStandardMarket(WorldAction world) =>
        world as EconomyTransaction ?? throw new InvalidOperationException("Standard market operations require the resident world owner.");

    private void WarmStandardMarket()
    {
        var live =
            LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Standard market readiness requires the resident world.");
        var snapshot = database.MarketSnapshot;
        long generation = snapshot.Manifest.CapturedAt.UtcTicks;
        var pending = live.Economy.Inspect(world =>
            snapshot
                .Manifest.Regions.Where(region => !IsStandardRegionSeeded(world, region.RegionId, generation))
                .Select(region => region.RegionId)
                .ToArray()
        );
        foreach (int region in pending)
        {
            _ = snapshot.RegionOrdersByType(region);
        }
        snapshot.CompleteReadiness();
    }

    // Negative region keys keep a complete region distinct from every real (region, type) marker.
    private static bool IsStandardRegionSeeded(WorldAction world, int regionId, long generation) =>
        world.Set<SavedStandardMarketType>().Find(-regionId, 0)?.SnapshotTicks == generation;

    private static void CompleteStandardRegion(EconomyTransaction world, int regionId, long generation)
    {
        var regionalMarker =
            world.Set<SavedStandardMarketType>().Find(-regionId, 0)
            ?? world.Add(new SavedStandardMarketType { RegionId = -regionId, TypeId = 0 }).Entity;
        regionalMarker.SnapshotTicks = generation;
    }

    private void PrepareStandardRegion(LiveWorld live, int regionId)
    {
        const int ordersPerBatch = 1_024;
        var snapshot = database.MarketSnapshot;
        long generation = snapshot.Manifest.CapturedAt.UtcTicks;
        // Concurrent reads of the same region share preparation without holding any world owner between batches.
        lock (snapshot.RegionPreparation(regionId))
        {
            var existing = live.Economy.Inspect(world =>
                IsStandardRegionSeeded(world, regionId, generation)
                    ? null
                    : ResidentStandardMarket(world).RegionalOrders(regionId).ToArray()
            );
            if (existing is null)
            {
                return;
            }
            using var phase = SessionPerformance.Phase("market.prepareStandardRegion");
            using CatalogDbContext catalog = new(database.CatalogPath);
            var sources = snapshot.RegionOrdersByType(regionId);
            var oldOrders = existing.Where(order => order.Kind == NpcMarketKind.Standard).ToArray();
            var stationIds = oldOrders.Select(order => order.StationId).ToHashSet();
            stationIds.UnionWith(sources.Values.SelectMany(orders => orders.Select(order => order.StationId)));
            using (var markers = live.Economy.Begin(TransactionParticipants.None))
            {
                var stationMarkers = markers.Set<MarketStation>();
                // Older saves kept the full generation on stations, including stations with retired orders.
                if (stationIds.All(station => stationMarkers.Find(station)?.StandardSnapshotTicks == generation))
                {
                    CompleteStandardRegion(markers, regionId, generation);
                    markers.Commit();
                    return;
                }
            }
            var oldCounts = oldOrders.GroupBy(order => order.TypeId).ToDictionary(group => group.Key, group => group.Count());
            var oldKeys = oldOrders.ToDictionary(order => (order.StationId, order.SourceOrderId));
            foreach (var source in sources.Values.SelectMany(orders => orders))
            {
                if (
                    oldKeys.TryGetValue((source.StationId, source.SourceOrderId), out var old)
                    && (old.TypeId != source.TypeId || old.Bid != source.Bid)
                )
                {
                    throw new InvalidDataException("A live market source identity changed its item or side.");
                }
            }
            List<int> batch = [];
            int rows = 0;
            foreach (int type in sources.Keys.Concat(oldCounts.Keys).Distinct())
            {
                int typeRows = Math.Max(sources.GetValueOrDefault(type)?.Length ?? 0, oldCounts.GetValueOrDefault(type)) + 1;
                if (batch.Count > 0 && rows + typeRows > ordersPerBatch)
                {
                    CommitBatch();
                    rows = 0;
                }
                batch.Add(type);
                rows += typeRows;
            }
            if (batch.Count > 0)
            {
                CommitBatch();
            }
            using var final = live.Economy.Begin(TransactionParticipants.None);
            var stations = final.Set<MarketStation>();
            long now = Now;
            foreach (int stationId in stationIds)
            {
                var station = stations.Find(stationId) ?? final.Add(new MarketStation { StationId = stationId, CreatedAt = now }).Entity;
                station.StandardSnapshotTicks = generation;
            }
            CompleteStandardRegion(final, regionId, generation);
            final.Commit();

            void CommitBatch()
            {
                using var world = live.Economy.Begin(TransactionParticipants.None);
                SeedStandardOrders(world, catalog, regionId, batch, sources);
                world.Commit();
                batch.Clear();
            }
        }
    }

    private void SeedStandardOrders(
        EconomyTransaction world,
        CatalogDbContext catalog,
        int regionId,
        IReadOnlyList<int>? types,
        IReadOnlyDictionary<int, LiveMarketOrder[]>? sourceOrders = null
    )
    {
        var snapshot = database.MarketSnapshot;
        long generation = snapshot.Manifest.CapturedAt.UtcTicks;
        // Negative keys reserve regional preparation separately from station supply.
        var preparation = new InstitutionalPreparation(-regionId, catalog.Definitions.Identity.Generation, 0, false, generation);
        if (world.Prepared(preparation))
        {
            return;
        }
        if (IsStandardRegionSeeded(world, regionId, generation))
        {
            world.Remember(preparation);
            return;
        }
        var typeMarkers = world.Set<SavedStandardMarketType>();
        bool TypeSeeded(int type) => typeMarkers.Find(regionId, type)?.SnapshotTicks == generation;

        // Quotes and trades need the requested types across the region, including ranged bids.
        // Keep their progress distinct from the station markers for a complete import.
        var pending = types?.Where(type => !TypeSeeded(type)).ToHashSet();
        if (pending is { Count: 0 })
        {
            return;
        }
        using var phase = SessionPerformance.Phase("market.seedStandardOrders");
        var region = sourceOrders ?? snapshot.RegionOrdersByType(regionId);
        var stations = world.Set<MarketStation>();
        Dictionary<int, MarketStation?> updatingStations = [];
        bool UpdateStation(int stationId)
        {
            if (updatingStations.ContainsKey(stationId))
            {
                return true;
            }
            var marker = stations.Find(stationId);
            if (marker?.StandardSnapshotTicks == generation)
            {
                return false;
            }
            updatingStations.Add(stationId, marker);
            return true;
        }

        var candidates = pending is null ? world.RegionalOrders(regionId) : world.RegionalOrdersForTypes(regionId, pending);
        var existing = candidates
            .Where(order => order.Kind == NpcMarketKind.Standard && (pending is not null || !TypeSeeded(order.TypeId)))
            .Where(order => UpdateStation(order.StationId))
            .ToDictionary(order => (order.StationId, order.SourceOrderId));
        IEnumerable<int> sources = pending is null ? region.Keys.Where(type => !TypeSeeded(type)) : pending;
        var orders = world.Set<NpcMarketOrder>();
        foreach (int type in sources)
        {
            foreach (var source in region.GetValueOrDefault(type) ?? [])
            {
                if (!UpdateStation(source.StationId))
                {
                    continue;
                }
                NpcMarketOrder order;
                if (existing.Remove((source.StationId, source.SourceOrderId), out var saved))
                {
                    if (saved.TypeId != source.TypeId || saved.Bid != source.Bid)
                    {
                        throw new InvalidDataException("A live market source identity changed its item or side.");
                    }
                    order = orders.Find(saved.Id)!;
                }
                else
                {
                    order = world
                        .Add(
                            new NpcMarketOrder
                            {
                                Kind = NpcMarketKind.Standard,
                                SourceOrderId = source.SourceOrderId,
                                StationId = source.StationId,
                                SystemId = source.SystemId,
                                RegionId = regionId,
                                OwnerId = catalog.RequireStation(source.StationId).OwnerId,
                                TypeId = source.TypeId,
                                Bid = source.Bid,
                            }
                        )
                        .Entity;
                }
                order.PriceCents = source.PriceCents;
                order.Remaining = source.Remaining;
                order.Entered = source.Entered;
                order.Issued = source.Issued;
                order.DurationDays = source.DurationDays;
                order.Range = source.Range;
                order.MinimumVolume = source.MinimumVolume;
            }
        }
        foreach (var retired in existing.Values.Where(order => order.Remaining > 0))
        {
            orders.Find(retired.Id)!.Remaining = 0;
        }
        if (pending is null)
        {
            foreach (var (station, saved) in updatingStations)
            {
                var marker = saved ?? world.Add(new MarketStation { StationId = station, CreatedAt = Now }).Entity;
                marker.StandardSnapshotTicks = generation;
            }
            CompleteStandardRegion(world, regionId, generation);
            world.Remember(preparation);
        }
        else
        {
            foreach (int type in pending)
            {
                var marker =
                    typeMarkers.Find(regionId, type)
                    ?? world.Add(new SavedStandardMarketType { RegionId = regionId, TypeId = type }).Entity;
                marker.SnapshotTicks = generation;
            }
        }
        world.SaveChanges();
    }

    private static NpcMarketKind MarketKind(WorldAction world) =>
        world.Settings.Economy.StandardMarket ? NpcMarketKind.Standard
        : world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false } ? NpcMarketKind.Institutional
        : NpcMarketKind.Development;
}
