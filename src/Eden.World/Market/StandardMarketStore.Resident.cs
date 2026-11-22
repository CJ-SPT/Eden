using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Runtime.Economy;

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

    private static readonly CatalogDefinition<IReadOnlyDictionary<int, int>> StandardMarketDistances = new(
        "standard-market.distances",
        (catalog, source) =>
        {
            Dictionary<int, int> distances = new() { [source] = 0 };
            Queue<int> pending = new();
            pending.Enqueue(source);
            while (pending.TryDequeue(out int system))
            {
                foreach (var gate in catalog.Definitions.GatesInSystem(system))
                {
                    if (distances.TryAdd(gate.DestinationSystemId, distances[system] + 1))
                    {
                        pending.Enqueue(gate.DestinationSystemId);
                    }
                }
            }
            return distances;
        },
        Estimate: distances => distances.Count * 32L
    );

    private static bool StandardBidReaches(CatalogDbContext catalog, NpcMarketOrder order, SavedPersonalOrder incoming)
    {
        if (!order.Bid || order.StationId == incoming.StationId || order.Range == StandardMarketRules.RegionRange)
        {
            return true;
        }
        if (order.Range == MarketDefinitions.StationRange)
        {
            return false;
        }
        var distances = catalog.Definitions.Definition(StandardMarketDistances, incoming.SystemId);
        return StandardMarketRules.Reaches(order.Range, false, distances.TryGetValue(order.SystemId, out int jumps) ? jumps : null);
    }

    private static EconomyTransaction ResidentStandardMarket(WorldAction world) =>
        world as EconomyTransaction ?? throw new InvalidOperationException("Standard market operations require the resident world owner.");

    private void SeedStandardOrders(EconomyTransaction world, CatalogDbContext catalog, int regionId, IReadOnlyList<int>? types)
    {
        var snapshot = database.MarketSnapshot;
        long generation = snapshot.Manifest.CapturedAt.UtcTicks;
        // Negative keys reserve regional preparation separately from station supply.
        var preparation = new InstitutionalPreparation(-regionId, catalog.Definitions.Identity.Generation, 0, false, generation);
        if (world.Prepared(preparation))
        {
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
        var region = snapshot.RegionOrdersByType(regionId);
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
