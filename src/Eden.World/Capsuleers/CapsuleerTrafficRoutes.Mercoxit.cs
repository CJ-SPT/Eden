using System.Globalization;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Mining;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerTrafficRoutes
{
    private const int MaximumExpeditionJumps = 8;
    private const int MaximumMercoxitDestinations = 4;
    private static readonly CatalogDefinition<IReadOnlyDictionary<int, int[][]>> s_mercoxitRoutes = new(
        "capsuleers/mercoxit-routes",
        (catalog, _) => LoadMercoxitRoutes(catalog)
    );
    private static readonly CatalogDefinition<bool> s_mercoxitBelt = new(
        "capsuleers/mercoxit-belt",
        (catalog, belt) => catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, belt) is { } counts && HasMercoxit(catalog, counts)
    );

    private sealed record MercoxitPath(int System, int Destination, int Depth, MercoxitPath? Next = null)
    {
        internal int[] Systems()
        {
            var systems = new int[Depth + 1];
            for (MercoxitPath? path = this; path is not null; path = path.Next)
            {
                systems[Depth - path.Depth] = path.System;
            }
            return systems;
        }
    }

    internal static IReadOnlyDictionary<int, int[][]> MercoxitRoutes(CatalogProvider catalog) => catalog.GlobalDefinition(s_mercoxitRoutes);

    internal static bool MercoxitBelt(CatalogDbContext catalog, int belt) => catalog.Definition(s_mercoxitBelt, belt);

    private static bool HasMercoxit(CatalogDbContext catalog, IReadOnlyDictionary<string, long> counts) =>
        counts.Any(entry =>
            entry.Value > 0
            && int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int type)
            && catalog.FittingType(type)?.GroupId == MiningDefinitions.MercoxitGroup
        );

    private static IReadOnlyDictionary<int, int[][]> LoadMercoxitRoutes(CatalogDbContext catalog)
    {
        const int laser = (int)MiningLaserType.DeepCoreMiningLaserI;
        if (catalog.FittingType(laser) is not { } tool || !MiningLaserCatalog.Supported(catalog, laser, tool.GroupId))
        {
            return new Dictionary<int, int[][]>();
        }
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        var destinations = catalog
            .BeltSystemsWithOreGroup(MiningDefinitions.MercoxitGroup)
            .Where(system => graph.Links.ContainsKey(system) && catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0)
            .Order()
            .ToArray();
        // One breadth-first search admits the nearest distinct deposits at each system.
        // Keep a bounded number of alternatives so a full or depleted deposit does not funnel every trip into one system.
        var starts = destinations.Select(system => new MercoxitPath(system, system, 0)).ToArray();
        Queue<MercoxitPath> pending = new(starts);
        var reached = starts.ToDictionary(path => path.System, path => new List<MercoxitPath> { path });
        while (pending.TryDequeue(out var next))
        {
            if (next.Depth == MaximumExpeditionJumps)
            {
                continue;
            }
            foreach (int system in graph.Links.GetValueOrDefault(next.System) ?? [])
            {
                if (!reached.TryGetValue(system, out var paths))
                {
                    paths = [];
                    reached.Add(system, paths);
                }
                if (paths.Count == MaximumMercoxitDestinations || paths.Exists(path => path.Destination == next.Destination))
                {
                    continue;
                }
                MercoxitPath path = new(system, next.Destination, next.Depth + 1, next);
                paths.Add(path);
                pending.Enqueue(path);
            }
        }
        return reached
            .Where(entry =>
                (graph.Stations.GetValueOrDefault(entry.Key) ?? []).Any(id =>
                    StationCapabilities.Has(catalog, catalog.RequireStation(id).OperationId, StationService.Market)
                )
            )
            .ToDictionary(entry => entry.Key, entry => entry.Value.Select(path => path.Systems()).ToArray());
    }
}
