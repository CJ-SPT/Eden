using Eden.Data.World.Market;
using Eden.Gameplay.Market;
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

    private void SeedStandardRegion(WorldAction world, CatalogDbContext catalog, int regionId)
    {
        var snapshot = database.MarketSnapshot;
        long generation = snapshot.Manifest.CapturedAt.UtcTicks;
        // Negative keys reserve regional preparation separately from station supply.
        var preparation = new InstitutionalPreparation(-regionId, catalog.Definitions.Identity.Generation, 0, false, generation);
        if (world.Prepared(preparation))
        {
            return;
        }
        var region = snapshot.Region(regionId);
        var stations = world.Set<MarketStation>();
        var previouslySeeded = world
            .Set<NpcMarketOrder>()
            .Where(order => order.RegionId == regionId && order.Kind == NpcMarketKind.Standard)
            .Select(order => order.StationId)
            .Distinct()
            .ToArray();
        foreach (int stationId in region.Keys.Concat(previouslySeeded).Distinct())
        {
            var marker = stations.Find(stationId);
            if (marker?.StandardSnapshotTicks == generation)
            {
                continue;
            }
            var station = catalog.RequireStation(stationId);
            var existing = world
                .Set<NpcMarketOrder>()
                .Where(order => order.StationId == stationId && order.Kind == NpcMarketKind.Standard)
                .ToDictionary(order => order.SourceOrderId);
            foreach (var source in region.GetValueOrDefault(stationId) ?? [])
            {
                if (!existing.Remove(source.SourceOrderId, out var order))
                {
                    order = world
                        .Add(
                            new NpcMarketOrder
                            {
                                Kind = NpcMarketKind.Standard,
                                SourceOrderId = source.SourceOrderId,
                                StationId = stationId,
                                SystemId = station.SolarSystemId,
                                RegionId = regionId,
                                OwnerId = station.OwnerId,
                                TypeId = source.TypeId,
                                Bid = source.Bid,
                            }
                        )
                        .Entity;
                }
                else if (order.TypeId != source.TypeId || order.Bid != source.Bid)
                {
                    throw new InvalidDataException("A live market source identity changed its item or side.");
                }
                order.PriceCents = source.PriceCents;
                order.Remaining = source.Remaining;
                order.Entered = source.Entered;
                order.Issued = source.Issued;
                order.DurationDays = source.DurationDays;
                order.Range = source.Range;
                order.MinimumVolume = source.MinimumVolume;
            }
            foreach (var retired in existing.Values)
            {
                retired.Remaining = 0;
            }
            marker ??= world.Add(new MarketStation { StationId = stationId, CreatedAt = Now }).Entity;
            marker.StandardSnapshotTicks = generation;
        }
        world.SaveChanges();
        world.Remember(preparation);
    }

    private static NpcMarketKind MarketKind(WorldAction world) =>
        world.Settings.Economy.StandardMarket ? NpcMarketKind.Standard
        : world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId) is { TestingMarket: false } ? NpcMarketKind.Institutional
        : NpcMarketKind.Development;
}
