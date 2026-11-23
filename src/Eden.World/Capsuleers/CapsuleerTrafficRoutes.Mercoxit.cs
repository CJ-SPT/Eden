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
    private static readonly CatalogDefinition<IReadOnlyDictionary<int, int[]>> s_mercoxitRoutes = new(
        "capsuleers/mercoxit-routes",
        (catalog, _) => LoadMercoxitRoutes(catalog)
    );
    private static readonly CatalogDefinition<bool> s_mercoxitBelt = new(
        "capsuleers/mercoxit-belt",
        (catalog, belt) => catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, belt) is { } counts && HasMercoxit(catalog, counts)
    );

    internal static IReadOnlyDictionary<int, int[]> MercoxitRoutes(CatalogProvider catalog) => catalog.GlobalDefinition(s_mercoxitRoutes);

    internal static bool MercoxitBelt(CatalogDbContext catalog, int belt) => catalog.Definition(s_mercoxitBelt, belt);

    private static bool HasMercoxit(CatalogDbContext catalog, IReadOnlyDictionary<string, long> counts) =>
        counts.Any(entry =>
            entry.Value > 0
            && int.TryParse(entry.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int type)
            && catalog.FittingType(type)?.GroupId == MiningDefinitions.MercoxitGroup
        );

    private static IReadOnlyDictionary<int, int[]> LoadMercoxitRoutes(CatalogDbContext catalog)
    {
        const int laser = (int)MiningLaserType.DeepCoreMiningLaserI;
        if (catalog.FittingType(laser) is not { } tool || !MiningLaserCatalog.Supported(catalog, laser, tool.GroupId))
        {
            return new Dictionary<int, int[]>();
        }
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        var destinations = catalog
            .BeltSystemsWithOreGroup(MiningDefinitions.MercoxitGroup)
            .Where(system => graph.Links.ContainsKey(system) && catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0)
            .Order()
            .ToArray();
        // Search from the deposits once. The graph contains only reciprocal gates with flight objects at both ends.
        Queue<(int System, int Depth)> pending = new(destinations.Select(system => (system, 0)));
        var towardDeposit = destinations.ToDictionary(system => system, system => system);
        Dictionary<int, int[]> routes = [];
        while (pending.TryDequeue(out var next))
        {
            if (
                (graph.Stations.GetValueOrDefault(next.System) ?? []).Any(id =>
                    StationCapabilities.Has(catalog, catalog.RequireStation(id).OperationId, StationService.Market)
                )
            )
            {
                routes.Add(next.System, PathTo(next.System, towardDeposit).Reverse().ToArray());
            }
            if (next.Depth == MaximumExpeditionJumps)
            {
                continue;
            }
            foreach (int system in graph.Links.GetValueOrDefault(next.System) ?? [])
            {
                if (towardDeposit.TryAdd(system, next.System))
                {
                    pending.Enqueue((system, next.Depth + 1));
                }
            }
        }
        return routes;
    }
}
