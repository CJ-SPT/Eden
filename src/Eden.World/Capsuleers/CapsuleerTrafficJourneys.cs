using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerTrafficRoutes
{
    private const int MaximumRegionalRoutes = 32;
    private const int OrdinarySystemWeight = 4;
    private const int RecentSystemWeight = 1;
    private const int HubStationWeight = 4;
    private const int JourneyNodeEstimatedBytes = 32;
    private const int NearbySearchDepth = 8;
    private const ulong StationDrawSalt = 0x9E3779B97F4A7C15UL;

    internal static CapsuleerTrafficRoute[] RegionalRoutes(
        CatalogDbContext catalog,
        int station,
        CapsuleerPopulationPolicy? policy = null
    ) =>
        catalog
            .Definition(s_journeys, station)
            .Where(route =>
                route.Systems.Length <= RoamingGangRules.MaximumJumps + 1
                && policy?.CanUse(catalog.Definitions, route) != false
                && route.Systems.All(system => catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0)
            )
            .OrderBy(route => route.Systems.Length)
            .ThenBy(route => route.DestinationStationId)
            .Take(MaximumRegionalRoutes)
            .ToArray();

    internal static CapsuleerTrafficRoute? Roam(
        CatalogDbContext catalog,
        int station,
        long seed,
        CapsuleerPopulationPolicy? policy = null
    ) =>
        catalog
            .Definition(s_journeys, station)
            .Where(route =>
                route.Systems.Length is > 1 and <= RoamingGangRules.MaximumJumps + 1
                && policy?.CanUse(catalog.Definitions, route) != false
                && route.Systems.All(system => catalog.Definitions.SystemSecurity.GetValueOrDefault(system, 1) <= 0)
            )
            .OrderBy(route => TravelerRoutineRules.Mix((ulong)route.DestinationStationId ^ (ulong)seed))
            .FirstOrDefault();

    private static readonly CatalogDefinition<CapsuleerTrafficRoute[]> s_journeys = new(
        "capsuleer-destination-journeys",
        LoadJourneys,
        Estimate: routes => EntryEstimatedBytes + routes.Sum(r => EntryEstimatedBytes + r.Systems.Length * sizeof(int))
    );

    private static readonly CatalogDefinition<JourneyMap> s_tradeHubPaths = new(
        "capsuleer-trade-hub-paths",
        (catalog, system) => LoadJourneyMap(catalog, system, unrestricted: true, maximumJumps: int.MaxValue),
        Estimate: map => map.EstimatedBytes
    );

    private readonly record struct JourneyNode(int Parent, int Jumps);

    private readonly record struct StationSystem(int Id, int Jumps);

    private sealed class JourneyMap(Dictionary<int, JourneyNode> nodes, StationSystem[] stationSystems)
    {
        // The search transfers its dictionary to this read-only map. Account for spare buckets in the catalog cache.
        internal IReadOnlyDictionary<int, JourneyNode> Nodes { get; } = nodes;
        internal StationSystem[] StationSystems { get; } = stationSystems;
        internal long EstimatedBytes { get; } =
            EntryEstimatedBytes + (long)nodes.EnsureCapacity(0) * JourneyNodeEstimatedBytes + stationSystems.Length * 2L * sizeof(int);
    }

    internal interface INearbyRoutes
    {
        IEnumerable<(int SystemId, int Jumps)> Systems { get; }

        int[]? PathTo(int destinationSystem, ulong draw);
    }

    private sealed class NearbyRoutes(TrafficGraph graph, JourneyMap map, int maximumJumps) : INearbyRoutes
    {
        public IEnumerable<(int SystemId, int Jumps)> Systems =>
            map.Nodes.Where(pair => pair.Value.Jumps <= maximumJumps).Select(pair => (pair.Key, pair.Value.Jumps));

        public int[]? PathTo(int destinationSystem, ulong draw) =>
            map.Nodes.TryGetValue(destinationSystem, out var node) && node.Jumps <= maximumJumps
                ? JourneyPath(graph, map, destinationSystem, draw)
                : null;
    }

    private sealed class JourneyMaps(bool unrestricted)
    {
        internal readonly CatalogDefinition<JourneyMap> Local = Define(unrestricted, TrafficJourneyKind.Local);
        internal readonly CatalogDefinition<JourneyMap> Regional = Define(unrestricted, TrafficJourneyKind.Regional);
        internal readonly CatalogDefinition<JourneyMap> LongDistance = Define(unrestricted, TrafficJourneyKind.LongDistance);
        internal readonly CatalogDefinition<JourneyMap> Nearby = Define(unrestricted, "nearby", NearbySearchDepth);

        private static CatalogDefinition<JourneyMap> Define(bool unrestricted, TrafficJourneyKind kind) =>
            Define(unrestricted, kind.ToString(), TrafficJourneyRules.MaximumDistance(kind));

        private static CatalogDefinition<JourneyMap> Define(bool unrestricted, string name, int depth) =>
            new(
                $"capsuleer-journey-{name}-{unrestricted}",
                (catalog, source) => LoadJourneyMap(catalog, source, unrestricted, depth),
                Estimate: map => map.EstimatedBytes
            );

        internal CatalogDefinition<JourneyMap> For(TrafficJourneyKind kind) =>
            kind switch
            {
                TrafficJourneyKind.Local => Local,
                TrafficJourneyKind.Regional => Regional,
                _ => LongDistance,
            };
    }

    private static readonly JourneyMaps s_journeyMaps = new(true);
    private static readonly JourneyMaps s_capitalJourneyMaps = new(false);

    internal static INearbyRoutes Nearby(
        CatalogDbContext catalog,
        int sourceSystemId,
        HullClass hull,
        int maximumJumps,
        IReadOnlySet<int>? avoided = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumJumps);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumJumps, TrafficJourneyRules.MaximumJumps);
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        if (!TrafficRules.RouteAllows(hull, false) || !catalog.Definitions.Systems.ContainsKey(sourceSystemId))
        {
            return new NearbyRoutes(graph, new([], []), maximumJumps);
        }

        bool unrestricted = TrafficRules.RouteAllows(hull, true);
        var maps = unrestricted ? s_journeyMaps : s_capitalJourneyMaps;
        var map = avoided is { Count: > 0 }
            ? LoadJourneyMap(catalog, sourceSystemId, unrestricted, maximumJumps, avoided)
            : catalog.Definition(maximumJumps <= NearbySearchDepth ? maps.Nearby : maps.LongDistance, sourceSystemId);
        return new NearbyRoutes(graph, map, maximumJumps);
    }

    internal static CapsuleerTrafficRoute? Journey(
        CatalogDbContext catalog,
        int stationId,
        TrafficJourneyKind kind,
        long sequence,
        int previousStationId,
        HullClass hull,
        TravelerRoutine? routine = null,
        long elapsed = 0,
        CapsuleerPopulationPolicy? policy = null
    )
    {
        if (!TrafficRules.RouteAllows(hull, false))
        {
            return null;
        }
        int source = catalog.RequireStation(stationId).SolarSystemId;
        bool unrestricted = TrafficRules.RouteAllows(hull, true);
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        var avoided = routine?.Threats.Where(threat => threat.ExpiresAt > elapsed).Select(threat => threat.SystemId).ToHashSet();
        var recentSystems = routine?.RecentStations.Select(id => catalog.Definitions.FindStation(id)?.SolarSystemId ?? 0).ToArray() ?? [];
        ulong draw = routine is null ? TravelerRoutineRules.Mix(unchecked((ulong)sequence)) : TravelerRoutineRules.Draw(routine, sequence);
        int maximum = TrafficJourneyRules.MaximumDistance(kind);
        var maps = unrestricted ? s_journeyMaps : s_capitalJourneyMaps;
        Predicate<int> onwardStation = candidate => candidate != stationId && candidate != previousStationId;
        var map = avoided is { Count: > 0 }
            ? LoadJourneyMap(catalog, source, unrestricted, maximum, avoided)
            : catalog.Definition(maps.For(kind), source);
        var (destination, onward) = ChooseDestination(map, avoided);
        if (destination == 0 && avoided is { Count: > 0 })
        {
            map = catalog.Definition(maps.For(kind), source);
            (destination, onward) = ChooseDestination(map, null);
        }
        if (destination == 0)
        {
            return null;
        }

        int station = 0;
        int stationWeight = 0;
        foreach (int candidate in graph.Stations[destination])
        {
            if (candidate == stationId || onward && candidate == previousStationId)
            {
                continue;
            }
            int geographic = CapsuleerEncounterRules.IsTradeHubStation(candidate) ? HubStationWeight : 1;
            int weight = routine is null ? geographic : TravelerRoutineRules.DestinationWeight(routine, candidate, geographic);
            stationWeight += weight;
            if (TravelerRoutineRules.Mix(draw ^ StationDrawSalt ^ (uint)candidate) % (ulong)stationWeight < (ulong)weight)
            {
                station = candidate;
            }
        }
        return new(stationId, station, JourneyPath(graph, map, destination, draw));

        (int System, bool Onward) ChooseDestination(JourneyMap reachable, IReadOnlySet<int>? excluded)
        {
            int selected = 0;
            (bool PreferredBand, bool Onward) bestPriority = default;
            int totalWeight = 0;
            int minimum = TrafficJourneyRules.MinimumDistance(kind);
            foreach (var stop in reachable.StationSystems)
            {
                int system = stop.Id;
                if (excluded?.Contains(system) == true || policy?.CanWork(catalog.Definitions, system) == false)
                {
                    continue;
                }
                var stations = graph.Stations[system];
                if (stations.Length == 1 && stations[0] == stationId)
                {
                    continue;
                }

                // Preserve local stops and preferred distance bands. Every eligible system in the winning band gets one draw.
                bool preferred = kind == TrafficJourneyKind.Local ? stop.Jumps == 0 : stop.Jumps >= minimum;
                var priority = (preferred, Array.Exists(stations, onwardStation));
                int comparison = priority.CompareTo(bestPriority);
                if (comparison < 0)
                {
                    continue;
                }
                if (comparison > 0)
                {
                    bestPriority = priority;
                    totalWeight = 0;
                }
                int weight = recentSystems.Contains(system) ? RecentSystemWeight : OrdinarySystemWeight;
                totalWeight += weight;
                if (TravelerRoutineRules.Mix(draw ^ (uint)system) % (ulong)totalWeight < (ulong)weight)
                {
                    selected = system;
                }
            }
            return (selected, bestPriority.Onward);
        }
    }

    internal static CapsuleerTrafficRoute? JourneyHome(
        CatalogDbContext catalog,
        int stationId,
        int homeStationId,
        HullClass hull,
        ulong draw,
        IReadOnlySet<int>? avoided = null
    )
    {
        if (!TrafficRules.RouteAllows(hull, false))
        {
            return null;
        }
        int source = catalog.RequireStation(stationId).SolarSystemId;
        int destination = catalog.RequireStation(homeStationId).SolarSystemId;
        bool unrestricted = TrafficRules.RouteAllows(hull, true);
        var map = avoided is { Count: > 0 }
            ? LoadJourneyMap(catalog, source, unrestricted, TrafficJourneyRules.MaximumJumps, avoided)
            : catalog.Definition((unrestricted ? s_journeyMaps : s_capitalJourneyMaps).LongDistance, source);
        if (!map.Nodes.ContainsKey(destination) || avoided?.Contains(destination) == true)
        {
            return null;
        }
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        return new(stationId, homeStationId, JourneyPath(graph, map, destination, draw));
    }

    internal static CapsuleerTrafficRoute? ToStation(
        CatalogDbContext catalog,
        int sourceStation,
        int destinationStation,
        HullClass hull,
        IReadOnlySet<int>? avoided = null,
        int maximumJumps = TrafficJourneyRules.MaximumJumps,
        CapsuleerPopulationPolicy? policy = null
    ) => ToSystem(catalog, sourceStation, destinationStation, hull, avoided, maximumJumps, policy: policy);

    internal static CapsuleerTrafficRoute? CapsuleToTradeHub(CatalogDbContext catalog, int sourceStation, int hub)
    {
        int source = catalog.RequireStation(sourceStation).SolarSystemId;
        int destination = catalog.RequireStation(hub).SolarSystemId;
        // The graph contains reciprocal gates. A single search from each hub serves every refuge station.
        var map = catalog.Definition(s_tradeHubPaths, destination);
        if (!map.Nodes.ContainsKey(source))
        {
            return null;
        }
        var systems = JourneyPath(catalog.Definitions.GlobalDefinition(s_graph), map, source);
        Array.Reverse(systems);
        return new(sourceStation, hub, systems);
    }

    internal static IEnumerable<CapsuleerTrafficRoute> AvoidTradeHubThreats(
        CatalogDbContext catalog,
        int sourceStation,
        CapsuleerTrafficRoute[] cached,
        IReadOnlySet<int> avoided
    )
    {
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        int source = catalog.RequireStation(sourceStation).SolarSystemId;
        JourneyMap? detours = null;
        List<CapsuleerTrafficRoute> routes = new(cached.Length);
        foreach (var route in cached)
        {
            if (!route.Systems.Skip(1).Any(avoided.Contains))
            {
                routes.Add(route);
                continue;
            }

            int destination = route.Systems[^1];
            if (avoided.Contains(destination))
            {
                continue;
            }

            detours ??= LoadJourneyMap(catalog, source, unrestricted: true, maximumJumps: int.MaxValue, avoided: avoided);
            if (detours.Nodes.ContainsKey(destination))
            {
                routes.Add(route with { Systems = JourneyPath(graph, detours, destination) });
            }
        }
        return routes.OrderBy(route => route.Systems.Length).ThenBy(route => route.DestinationStationId);
    }

    internal static CapsuleerTrafficRoute? ToClaimSystem(
        CatalogDbContext catalog,
        int headquarters,
        int system,
        HullClass hull,
        IReadOnlySet<int> avoided,
        int maximumJumps = CapsuleerAllianceRules.FirstClaimMaximumJumps,
        CapsuleerPopulationPolicy? policy = null
    ) => ToSystem(catalog, headquarters, headquarters, hull, avoided, maximumJumps, system, policy);

    private static CapsuleerTrafficRoute? ToSystem(
        CatalogDbContext catalog,
        int sourceStation,
        int destinationStation,
        HullClass hull,
        IReadOnlySet<int>? avoided,
        int maximumJumps,
        int? destinationSystem = null,
        CapsuleerPopulationPolicy? policy = null
    )
    {
        if (!TrafficRules.RouteAllows(hull, false))
        {
            return null;
        }
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        int source = catalog.RequireStation(sourceStation).SolarSystemId;
        int destination = destinationSystem ?? catalog.RequireStation(destinationStation).SolarSystemId;
        var security = catalog.Definitions.SystemSecurity;
        bool unrestricted = TrafficRules.RouteAllows(hull, true);
        bool Allows(int system) =>
            unrestricted || TrafficRules.SecurityBand(security.GetValueOrDefault(system)) != TrafficSecurityBand.High;
        if (
            !Allows(source)
            || !Allows(destination)
            || avoided?.Contains(destination) == true
            || policy is not null && (!policy.CanWork(catalog.Definitions, source) || !policy.CanWork(catalog.Definitions, destination))
        )
        {
            return null;
        }
        Queue<int> pending = new();
        Dictionary<int, int> parents = new() { [source] = source };
        Dictionary<int, int> distances = new() { [source] = 0 };
        pending.Enqueue(source);
        while (pending.TryDequeue(out int current))
        {
            if (current == destination)
            {
                return new(sourceStation, destinationStation, PathTo(current, parents));
            }
            if (distances[current] >= maximumJumps)
            {
                continue;
            }
            foreach (int next in graph.Links.GetValueOrDefault(current) ?? [])
            {
                if (!parents.ContainsKey(next) && Allows(next) && avoided?.Contains(next) != true)
                {
                    parents.Add(next, current);
                    distances[next] = distances[current] + 1;
                    pending.Enqueue(next);
                }
            }
        }
        return null;
    }

    private static CapsuleerTrafficRoute[] LoadJourneys(CatalogDbContext catalog, int stationId)
    {
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        int source = catalog.RequireStation(stationId).SolarSystemId;
        var map = catalog.Definition(s_journeyMaps.LongDistance, source);
        // Patrol planning keeps its bounded, geographically prioritized list.
        return map
            .StationSystems.SelectMany(stop => graph.Stations[stop.Id].Select(station => (System: stop.Id, Station: station, stop.Jumps)))
            .Where(stop => stop.Station != stationId)
            .OrderByDescending(stop => CapsuleerEncounterRules.IsTradeHubStation(stop.Station))
            .ThenByDescending(stop => graph.Stations[stop.System].Length)
            .ThenBy(stop => stop.Station)
            .GroupBy(stop => stop.Jumps)
            .SelectMany(group => group.Take(MaximumRoutes))
            .Select(stop => new CapsuleerTrafficRoute(stationId, stop.Station, JourneyPath(graph, map, stop.System)))
            .ToArray();
    }

    private static JourneyMap LoadJourneyMap(
        CatalogDbContext catalog,
        int source,
        bool unrestricted,
        int maximumJumps,
        IReadOnlySet<int>? avoided = null
    )
    {
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        var security = catalog.Definitions.SystemSecurity;
        bool Allows(int system) =>
            unrestricted || TrafficRules.SecurityBand(security.GetValueOrDefault(system)) != TrafficSecurityBand.High;
        if (!Allows(source))
        {
            return new([], []);
        }
        Queue<int> pending = new();
        Dictionary<int, JourneyNode> nodes = new() { [source] = new(source, 0) };
        List<StationSystem> stations = [];
        pending.Enqueue(source);
        while (pending.TryDequeue(out int current))
        {
            int jumps = nodes[current].Jumps;
            if (graph.Stations.ContainsKey(current))
            {
                stations.Add(new(current, jumps));
            }
            if (jumps >= maximumJumps)
            {
                continue;
            }
            foreach (int next in graph.Links.GetValueOrDefault(current) ?? [])
            {
                if (Allows(next) && avoided?.Contains(next) != true && nodes.TryAdd(next, new(current, jumps + 1)))
                {
                    pending.Enqueue(next);
                }
            }
        }
        return new(nodes, stations.ToArray());
    }

    private static int[] JourneyPath(TrafficGraph graph, JourneyMap map, int destination, ulong? draw = null)
    {
        int[] path = new int[map.Nodes[destination].Jumps + 1];
        int current = destination;
        for (int index = path.Length - 1; index > 0; index--)
        {
            path[index] = current;
            int parent = map.Nodes[current].Parent;
            if (draw is ulong seed)
            {
                // Any neighbor one layer closer gives a shortest route. Mix the trip and both systems to vary each fork.
                ulong best = ulong.MaxValue;
                foreach (int neighbor in graph.Links[current])
                {
                    if (map.Nodes.TryGetValue(neighbor, out var node) && node.Jumps == index - 1)
                    {
                        ulong rank = TravelerRoutineRules.Mix(seed ^ ((ulong)(uint)current << 32) ^ (uint)neighbor);
                        if (rank < best)
                        {
                            best = rank;
                            parent = neighbor;
                        }
                    }
                }
            }
            current = parent;
        }
        path[0] = current;
        return path;
    }
}
