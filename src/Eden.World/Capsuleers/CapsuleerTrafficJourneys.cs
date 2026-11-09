using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerTrafficRoutes
{
    private static readonly CatalogDefinition<CapsuleerTrafficRoute[]> s_journeys = new(
        "capsuleer-destination-journeys",
        LoadJourneys,
        Estimate: routes => EntryEstimatedBytes + routes.Sum(r => EntryEstimatedBytes + r.Systems.Length * sizeof(int))
    );

    private static readonly CatalogDefinition<CapsuleerTrafficRoute[]> s_capitalJourneys = new(
        "capsuleer-capital-destination-journeys",
        (catalog, station) => LoadJourneys(catalog, station, false),
        Estimate: routes => EntryEstimatedBytes + routes.Sum(r => EntryEstimatedBytes + r.Systems.Length * sizeof(int))
    );

    internal static CapsuleerTrafficRoute? Journey(
        CatalogDbContext catalog,
        int stationId,
        TrafficJourneyKind kind,
        long sequence,
        int previousStationId,
        HullClass hull,
        TravelerRoutine? routine = null,
        long elapsed = 0
    )
    {
        if (!TrafficRules.RouteAllows(hull, false))
        {
            return null;
        }
        bool unrestricted = TrafficRules.RouteAllows(hull, true);
        var routes = catalog.Definition(unrestricted ? s_journeys : s_capitalJourneys, stationId);
        var avoided = routine?.Threats.Where(threat => threat.ExpiresAt > elapsed).Select(threat => threat.SystemId).ToHashSet();
        int maximum = TrafficJourneyRules.MaximumDistance(kind);
        var candidates = routes.Where(route => route.Systems.Length - 1 <= maximum).ToArray();
        if (avoided is { Count: > 0 })
        {
            var alternatives = LoadJourneys(catalog, stationId, unrestricted, avoided)
                .Where(route => route.Systems.Length - 1 <= maximum && !avoided.Contains(route.Systems[^1]))
                .ToArray();
            if (alternatives.Length > 0)
            {
                candidates = alternatives;
            }
        }
        if (candidates.Length == 0)
        {
            return null;
        }
        if (kind == TrafficJourneyKind.Local)
        {
            var inSystem = candidates.Where(route => route.Systems.Length == 1).ToArray();
            if (inSystem.Length > 0)
            {
                candidates = inSystem;
            }
        }
        var preferred = candidates.Where(route => route.Systems.Length - 1 >= TrafficJourneyRules.MinimumDistance(kind)).ToArray();
        if (preferred.Length > 0)
        {
            candidates = preferred;
        }
        var onward = candidates.Where(route => route.DestinationStationId != previousStationId).ToArray();
        if (onward.Length > 0)
        {
            candidates = onward;
        }
        int Weight(CapsuleerTrafficRoute route)
        {
            int geographic =
                CapsuleerEncounterRules.IsTradeHubStation(route.DestinationStationId) ? 4
                : catalog.Definitions.StationsInSystem(route.Systems[^1]).Length >= 4 ? 2
                : 1;
            return routine is null ? geographic : TravelerRoutineRules.DestinationWeight(routine, route.DestinationStationId, geographic);
        }
        ulong draw = routine is null ? TravelerRoutineRules.Mix(unchecked((ulong)sequence)) : TravelerRoutineRules.Draw(routine, sequence);
        int selection = (int)(draw % (ulong)candidates.Sum(Weight));
        foreach (var candidate in candidates)
        {
            selection -= Weight(candidate);
            if (selection < 0)
            {
                return candidate;
            }
        }
        throw new InvalidOperationException("The destination weighting has no selected route.");
    }

    internal static CapsuleerTrafficRoute? ToStation(
        CatalogDbContext catalog,
        int sourceStation,
        int destinationStation,
        HullClass hull,
        IReadOnlySet<int>? avoided = null
    )
    {
        if (!TrafficRules.RouteAllows(hull, false))
        {
            return null;
        }
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        int source = catalog.RequireStation(sourceStation).SolarSystemId;
        int destination = catalog.RequireStation(destinationStation).SolarSystemId;
        var security = catalog.Definitions.SystemSecurity;
        bool unrestricted = TrafficRules.RouteAllows(hull, true);
        bool Allows(int system) =>
            unrestricted || TrafficRules.SecurityBand(security.GetValueOrDefault(system)) != TrafficSecurityBand.High;
        if (!Allows(source) || !Allows(destination) || avoided?.Contains(destination) == true)
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
            if (distances[current] >= TrafficJourneyRules.MaximumJumps)
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

    private static CapsuleerTrafficRoute[] LoadJourneys(CatalogDbContext catalog, int stationId) => LoadJourneys(catalog, stationId, true);

    private static CapsuleerTrafficRoute[] LoadJourneys(
        CatalogDbContext catalog,
        int stationId,
        bool unrestricted,
        IReadOnlySet<int>? avoided = null
    )
    {
        var graph = catalog.Definitions.GlobalDefinition(s_graph);
        int source = catalog.RequireStation(stationId).SolarSystemId;
        var security = catalog.Definitions.SystemSecurity;
        bool Allows(int system) =>
            unrestricted || TrafficRules.SecurityBand(security.GetValueOrDefault(system)) != TrafficSecurityBand.High;
        if (!Allows(source))
        {
            return [];
        }
        Queue<int> pending = new();
        Dictionary<int, int> parents = new() { [source] = source };
        Dictionary<int, int> distances = new() { [source] = 0 };
        List<CapsuleerTrafficRoute> routes = [];
        pending.Enqueue(source);
        while (pending.TryDequeue(out int current))
        {
            foreach (int destination in graph.Stations.GetValueOrDefault(current) ?? [])
            {
                if (destination != stationId)
                {
                    routes.Add(new(stationId, destination, PathTo(current, parents)));
                }
            }
            if (distances[current] >= TrafficJourneyRules.MaximumJumps)
            {
                continue;
            }
            foreach (int next in graph.Links.GetValueOrDefault(current) ?? [])
            {
                if (Allows(next) && avoided?.Contains(next) != true && parents.TryAdd(next, current))
                {
                    distances[next] = distances[current] + 1;
                    pending.Enqueue(next);
                }
            }
        }
        // Every distance band retains endpoints. Hubs and station clusters receive first choice
        // within a band without excluding quieter destinations.
        return routes
            .OrderByDescending(route => CapsuleerEncounterRules.IsTradeHubStation(route.DestinationStationId))
            .ThenByDescending(route => graph.Stations[route.Systems[^1]].Length)
            .ThenBy(route => route.DestinationStationId)
            .GroupBy(route => route.Systems.Length)
            .SelectMany(group => group.Take(MaximumRoutes))
            .ToArray();
    }
}
