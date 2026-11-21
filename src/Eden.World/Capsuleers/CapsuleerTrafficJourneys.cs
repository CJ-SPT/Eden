using System.Collections.Frozen;
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

    private readonly record struct JourneyNode(int Parent, int Jumps);

    private sealed record JourneyMap(FrozenDictionary<int, JourneyNode> Nodes, int[] StationSystems)
    {
        internal long EstimatedBytes =>
            EntryEstimatedBytes + (long)Nodes.Count * JourneyNodeEstimatedBytes + StationSystems.Length * sizeof(int);
    }

    private static readonly CatalogDefinition<JourneyMap> s_journeyMap = new(
        "capsuleer-journey-systems",
        (catalog, source) => LoadJourneyMap(catalog, source, true),
        Estimate: map => map.EstimatedBytes
    );

    private static readonly CatalogDefinition<JourneyMap> s_capitalJourneyMap = new(
        "capsuleer-capital-journey-systems",
        (catalog, source) => LoadJourneyMap(catalog, source, false),
        Estimate: map => map.EstimatedBytes
    );

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
        var map = catalog.Definition(unrestricted ? s_journeyMap : s_capitalJourneyMap, source);
        Predicate<int> onwardStation = candidate => candidate != stationId && candidate != previousStationId;
        int destination = 0;
        bool onward = false;
        if (avoided is { Count: > 0 })
        {
            var safe = LoadJourneyMap(catalog, source, unrestricted, avoided);
            (destination, onward) = ChooseDestination(safe, avoided);
            if (destination != 0)
            {
                map = safe;
            }
        }
        if (destination == 0)
        {
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
            int maximum = TrafficJourneyRules.MaximumDistance(kind);
            int minimum = TrafficJourneyRules.MinimumDistance(kind);
            foreach (int system in reachable.StationSystems)
            {
                int jumps = reachable.Nodes[system].Jumps;
                if (jumps > maximum)
                {
                    break;
                }
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
                bool preferred = kind == TrafficJourneyKind.Local ? jumps == 0 : jumps >= minimum;
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
            ? LoadJourneyMap(catalog, source, unrestricted, avoided)
            : catalog.Definition(unrestricted ? s_journeyMap : s_capitalJourneyMap, source);
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
        var map = catalog.Definition(s_journeyMap, source);
        // Patrol planning keeps its bounded, geographically prioritized list.
        return map
            .StationSystems.SelectMany(system => graph.Stations[system].Select(station => (System: system, Station: station)))
            .Where(stop => stop.Station != stationId)
            .OrderByDescending(stop => CapsuleerEncounterRules.IsTradeHubStation(stop.Station))
            .ThenByDescending(stop => graph.Stations[stop.System].Length)
            .ThenBy(stop => stop.Station)
            .GroupBy(stop => map.Nodes[stop.System].Jumps)
            .SelectMany(group => group.Take(MaximumRoutes))
            .Select(stop => new CapsuleerTrafficRoute(stationId, stop.Station, JourneyPath(graph, map, stop.System)))
            .ToArray();
    }

    private static JourneyMap LoadJourneyMap(CatalogDbContext catalog, int source, bool unrestricted, IReadOnlySet<int>? avoided = null)
    {
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        var security = catalog.Definitions.SystemSecurity;
        bool Allows(int system) =>
            unrestricted || TrafficRules.SecurityBand(security.GetValueOrDefault(system)) != TrafficSecurityBand.High;
        if (!Allows(source))
        {
            return new(FrozenDictionary<int, JourneyNode>.Empty, []);
        }
        Queue<int> pending = new();
        Dictionary<int, JourneyNode> nodes = new() { [source] = new(source, 0) };
        List<int> stations = [];
        pending.Enqueue(source);
        while (pending.TryDequeue(out int current))
        {
            if (graph.Stations.ContainsKey(current))
            {
                stations.Add(current);
            }
            int jumps = nodes[current].Jumps;
            if (jumps >= TrafficJourneyRules.MaximumJumps)
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
        return new(nodes.ToFrozenDictionary(), stations.ToArray());
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
