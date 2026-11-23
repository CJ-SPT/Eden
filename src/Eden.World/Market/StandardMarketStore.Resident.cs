using System.Buffers;
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

    private sealed record StandardGateGraph(IReadOnlyDictionary<int, int> Systems, int[][] Edges);

    private static readonly CatalogDefinition<StandardGateGraph> StandardMarketGraph = new(
        "standard-market.gate-graph",
        (catalog, _) =>
        {
            var definitions = catalog.Definitions;
            int[] systems = definitions
                .Gates.Values.SelectMany(gate => new[] { gate.SolarSystemId, gate.DestinationSystemId })
                .Distinct()
                .ToArray();
            var index = systems.Select((system, position) => (system, position)).ToDictionary(pair => pair.system, pair => pair.position);
            int[][] edges = systems
                .Select(system => definitions.GatesInSystem(system).Select(gate => index[gate.DestinationSystemId]).ToArray())
                .ToArray();
            return new(index, edges);
        },
        Estimate: graph => graph.Systems.Count * 64L + graph.Edges.Sum(edges => edges.Length * sizeof(int))
    );

    private static readonly CatalogDefinition<int[]> StandardMarketDistances = new(
        "standard-market.distances",
        (catalog, source) =>
        {
            var graph = catalog.Definitions.GlobalDefinition(StandardMarketGraph);
            int[] distances = new int[graph.Edges.Length];
            Array.Fill(distances, -1);
            int[] pending = ArrayPool<int>.Shared.Rent(graph.Edges.Length);
            try
            {
                int origin = graph.Systems[source];
                distances[origin] = 0;
                pending[0] = origin;
                int tail = 1;
                for (int head = 0; head < tail; head++)
                {
                    int system = pending[head];
                    foreach (int destination in graph.Edges[system])
                    {
                        if (distances[destination] == -1)
                        {
                            distances[destination] = distances[system] + 1;
                            pending[tail++] = destination;
                        }
                    }
                }
                return distances;
            }
            finally
            {
                ArrayPool<int>.Shared.Return(pending);
            }
        },
        Estimate: distances => distances.Length * sizeof(int)
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
        if (order.SystemId == incoming.SystemId)
        {
            return StandardMarketRules.Reaches(order.Range, false, 0);
        }
        var definitions = catalog.Definitions;
        var graph = definitions.GlobalDefinition(StandardMarketGraph);
        if (!graph.Systems.ContainsKey(incoming.SystemId) || !graph.Systems.TryGetValue(order.SystemId, out int destination))
        {
            return false;
        }
        int distance = definitions.Definition(StandardMarketDistances, incoming.SystemId)[destination];
        return distance >= 0 && StandardMarketRules.Reaches(order.Range, false, distance);
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
