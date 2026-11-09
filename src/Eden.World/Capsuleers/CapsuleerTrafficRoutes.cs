using System.Collections.Frozen;
using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerTrafficRoute(int SourceStationId, int DestinationStationId, int[] Systems);

internal sealed record CapsuleerTrafficPlan(int GateCount, int StationCount, CapsuleerTrafficRoute[] Routes);

internal static partial class CapsuleerTrafficRoutes
{
    private const int GraphKey = 0;
    private const int MaximumRoutes = 8;
    private const int MaximumEndpoints = 16;
    private const int DesiredStationSystems = 4;
    private const long EntryEstimatedBytes = 256;
    private static readonly CatalogDefinition<TrafficGraph> s_graph = new(
        "capsuleer-traffic-graph",
        LoadGraph,
        Estimate: graph =>
            EntryEstimatedBytes
            + graph.Links.Sum(p => EntryEstimatedBytes + p.Value.Length * sizeof(int))
            + graph.Stations.Sum(p => EntryEstimatedBytes + p.Value.Length * sizeof(int))
    );
    private static readonly CatalogDefinition<CapsuleerTrafficPlan> s_plans = new(
        "capsuleer-traffic-routes",
        LoadPlan,
        Estimate: plan => EntryEstimatedBytes + plan.Routes.Sum(r => EntryEstimatedBytes + r.Systems.Length * sizeof(int))
    );

    private sealed record TrafficGraph(FrozenDictionary<int, int[]> Links, FrozenDictionary<int, int[]> Stations);

    private sealed record StationEndpoint(int StationId, int SystemId, int Branch);

    private sealed record StationPath(int StationId, int[] Systems);

    internal static CapsuleerTrafficPlan Read(CatalogDbContext catalog, int systemId) => catalog.Definition(s_plans, systemId);

    internal static CapsuleerTrafficRoute? Inbound(
        CatalogDbContext catalog,
        WatchedTrafficGate gate,
        bool sourceObserved = false,
        int visitor = 0
    )
    {
        if (
            gate.ArrivalGateId is <= 0 or > int.MaxValue
            || gate.DepartureGateId is <= 0 or > int.MaxValue
            || gate.ObserverSystemId == gate.SourceSystemId
        )
        {
            return null;
        }
        var definitions = catalog.Definitions;
        if (
            !definitions.Gates.TryGetValue((int)gate.ArrivalGateId, out var arrival)
            || !definitions.Gates.TryGetValue((int)gate.DepartureGateId, out var departure)
            || arrival.SolarSystemId != gate.ObserverSystemId
            || departure.SolarSystemId != gate.SourceSystemId
            || arrival.DestinationId != departure.Id
            || departure.DestinationId != arrival.Id
            || arrival.DestinationSystemId != departure.SolarSystemId
            || departure.DestinationSystemId != arrival.SolarSystemId
        )
        {
            return null;
        }
        var arrivalBody = definitions.FindCelestial(arrival.Id);
        var departureBody = definitions.FindCelestial(departure.Id);
        if (
            arrivalBody?.SolarSystemId != gate.ObserverSystemId
            || departureBody?.SolarSystemId != gate.SourceSystemId
            || !definitions.Items.ContainsKey(arrivalBody.TypeId)
            || !definitions.Items.ContainsKey(departureBody.TypeId)
        )
        {
            return null;
        }
        var graph = catalog.Definition(s_graph, GraphKey);
        var anchor = FindStationPath(graph, gate.SourceSystemId, gate.ObserverSystemId);
        if (anchor is null || anchor.Systems.Length > TrafficJourneyRules.MaximumJumps)
        {
            return null;
        }
        var blocked = anchor.Systems.ToHashSet();
        Queue<int> pending = new();
        Dictionary<int, int> parents = new() { [gate.ObserverSystemId] = gate.ObserverSystemId };
        pending.Enqueue(gate.ObserverSystemId);
        List<StationPath> destinations = [];
        while (pending.TryDequeue(out int current))
        {
            var path = PathTo(current, parents);
            foreach (int station in (graph.Stations.GetValueOrDefault(current) ?? []).Take(2))
            {
                destinations.Add(new(station, path));
            }
            if (path.Length + anchor.Systems.Length >= TrafficJourneyRules.MaximumJumps + 1)
            {
                continue;
            }
            foreach (int next in graph.Links.GetValueOrDefault(current) ?? [])
            {
                if (!blocked.Contains(next) && parents.TryAdd(next, current))
                {
                    pending.Enqueue(next);
                }
            }
        }
        if (destinations.Count == 0)
        {
            return null;
        }
        int minimum = visitor % 2 == 0 ? TrafficJourneyRules.RegionalMinimumJumps : TrafficJourneyRules.LongDistanceMinimumJumps;
        var preferred = destinations.Where(path => path.Systems.Length + anchor.Systems.Length - 1 >= minimum).ToArray();
        var choices = preferred.Length > 0 ? preferred : destinations.ToArray();
        var destination = choices[visitor % choices.Length];
        return new(anchor.StationId, destination.StationId, [.. anchor.Systems.Reverse(), .. destination.Systems]);
    }

    private static StationPath? FindStationPath(TrafficGraph graph, int root, int blockedNeighbor)
    {
        Queue<int> pending = new();
        Dictionary<int, int> parents = new() { [root] = root };
        pending.Enqueue(root);
        while (pending.TryDequeue(out int current))
        {
            if (graph.Stations.TryGetValue(current, out var stations) && stations.Length > 0)
            {
                return new(stations[0], PathTo(current, parents));
            }
            foreach (int next in graph.Links.GetValueOrDefault(current) ?? [])
            {
                if (next == blockedNeighbor)
                {
                    continue;
                }
                if (parents.TryAdd(next, current))
                {
                    pending.Enqueue(next);
                }
            }
        }
        return null;
    }

    private static TrafficGraph LoadGraph(CatalogDbContext catalog, int unused)
    {
        var definitions = catalog.Definitions;
        // A map link without a static gate object cannot be approached by the
        // flight simulation. Disrupted gates never enter the Stargate table.
        var available = catalog.StaticGateSystems();
        var links = definitions
            .Gates.Values.Where(gate => gate.SolarSystemId != gate.DestinationSystemId)
            .Where(gate => definitions.Systems.ContainsKey(gate.SolarSystemId) && definitions.Systems.ContainsKey(gate.DestinationSystemId))
            .Where(gate => available.TryGetValue(gate.Id, out int system) && system == gate.SolarSystemId)
            .Where(gate => available.TryGetValue(gate.DestinationId, out int system) && system == gate.DestinationSystemId)
            .Where(gate =>
                definitions.Gates.TryGetValue(gate.DestinationId, out var other)
                && other.DestinationId == gate.Id
                && other.DestinationSystemId == gate.SolarSystemId
            )
            .GroupBy(gate => gate.SolarSystemId)
            .ToFrozenDictionary(group => group.Key, group => group.Select(gate => gate.DestinationSystemId).Distinct().Order().ToArray());
        var stations = definitions
            .Stations.Values.Where(station =>
                definitions.Systems.ContainsKey(station.SolarSystemId) && definitions.Items.ContainsKey(station.TypeId)
            )
            .GroupBy(station => station.SolarSystemId)
            .ToFrozenDictionary(
                group => group.Key,
                group =>
                    group
                        .Select(station => station.Id)
                        .OrderByDescending(CapsuleerEncounterRules.IsTradeHubStation)
                        .ThenBy(id => id)
                        .ToArray()
            );
        return new(links, stations);
    }

    private static CapsuleerTrafficPlan LoadPlan(CatalogDbContext catalog, int systemId)
    {
        var graph = catalog.Definition(s_graph, GraphKey);
        Dictionary<int, int> parents = new() { [systemId] = systemId };
        Dictionary<int, int> branches = new() { [systemId] = systemId };
        Queue<int> pending = new();
        List<StationEndpoint> stations = [];
        HashSet<int> stationSystems = [];
        HashSet<int> stationBranches = [];
        pending.Enqueue(systemId);
        // One breadth-first traversal per entered system replaces repeated
        // station-pair searches. No security threshold excludes transit space.
        while (pending.TryDequeue(out int current))
        {
            foreach (int station in graph.Stations.GetValueOrDefault(current) ?? [])
            {
                stations.Add(new(station, current, branches[current]));
                stationSystems.Add(current);
                stationBranches.Add(branches[current]);
            }
            foreach (int next in graph.Links.GetValueOrDefault(current) ?? [])
            {
                if (parents.TryAdd(next, current))
                {
                    branches[next] = current == systemId ? next : branches[current];
                    pending.Enqueue(next);
                }
            }
            bool throughRoute =
                stationSystems.Contains(systemId)
                || stationBranches.Count > 1
                || (graph.Links.GetValueOrDefault(systemId)?.Length ?? 0) <= 1;
            if (stations.Count >= MaximumEndpoints && stationSystems.Count >= DesiredStationSystems && throughRoute)
            {
                break;
            }
        }
        List<CapsuleerTrafficRoute> routes = [];
        if (stations.Count > 1)
        {
            // Round-robin branch selection gives sparse corridors endpoint
            // variety even when the nearest station system has many stations.
            var groups = stations.GroupBy(station => station.Branch).Select(group => group.ToArray()).ToArray();
            List<StationEndpoint> endpoints = stations.Where(station => station.SystemId == systemId).Take(2).ToList();
            for (int index = 0; endpoints.Count < MaximumEndpoints; index++)
            {
                bool found = false;
                foreach (var group in groups)
                {
                    if (index < group.Length)
                    {
                        found = true;
                        if (endpoints.Count < MaximumEndpoints && !endpoints.Contains(group[index]))
                        {
                            endpoints.Add(group[index]);
                        }
                    }
                }
                if (!found)
                {
                    break;
                }
            }
            AddStationRoutes(endpoints, parents, systemId, routes);
        }
        return new(
            graph.Links.GetValueOrDefault(systemId)?.Length ?? 0,
            graph.Stations.GetValueOrDefault(systemId)?.Length ?? 0,
            routes
                .Where(route =>
                    route.SourceStationId != route.DestinationStationId
                    && route.Systems.Length <= TrafficJourneyRules.MaximumJumps + 1
                    && route.Systems.Distinct().Count() == route.Systems.Length
                )
                .ToArray()
        );
    }

    private static void AddStationRoutes(
        List<StationEndpoint> endpoints,
        Dictionary<int, int> parents,
        int systemId,
        List<CapsuleerTrafficRoute> routes
    )
    {
        var local = endpoints.Where(endpoint => endpoint.SystemId == systemId).Take(2).ToArray();
        if (local.Length == 2)
        {
            routes.Add(new(local[0].StationId, local[1].StationId, [systemId]));
            routes.Add(new(local[1].StationId, local[0].StationId, [systemId]));
        }
        for (int fallback = 0; fallback < 2 && (fallback == 0 || routes.Count == 0); fallback++)
        {
            for (int offset = 1; offset < endpoints.Count && routes.Count < MaximumRoutes; offset++)
            {
                for (int index = 0; index + offset < endpoints.Count && routes.Count < MaximumRoutes; index++)
                {
                    var source = endpoints[index];
                    var destination = endpoints[index + offset];
                    if (
                        routes.Any(route =>
                            route.SourceStationId == source.StationId && route.DestinationStationId == destination.StationId
                        )
                    )
                    {
                        continue;
                    }
                    bool sharedBranch = source.Branch == destination.Branch && source.Branch != systemId;
                    if (sharedBranch && fallback == 0)
                    {
                        continue;
                    }
                    // Dead-end stationless systems require retracing a branch
                    // after visiting the turnaround; every leg remains real.
                    int[] path = PathTo(source.SystemId, parents).Reverse().Concat(PathTo(destination.SystemId, parents).Skip(1)).ToArray();
                    routes.Add(new(source.StationId, destination.StationId, path));
                    routes.Add(new(destination.StationId, source.StationId, path.Reverse().ToArray()));
                }
            }
        }
    }

    private static int[] PathTo(int destination, Dictionary<int, int> parents)
    {
        List<int> path = [destination];
        while (parents[destination] != destination)
        {
            destination = parents[destination];
            path.Add(destination);
        }
        path.Reverse();
        return path.ToArray();
    }
}
