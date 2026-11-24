using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal sealed record NullsecDestination(int[] Systems, NullsecExpeditionWork Work);

internal static partial class CapsuleerTrafficRoutes
{
    private const ulong QuietRoamingDestinationPenalty = 4;

    internal static bool IsNullsec(CatalogProvider catalog, int system) => catalog.SystemContentSecurity(system) is <= 0;

    internal static NullsecDestination? NullsecExpedition(
        CatalogDbContext catalog,
        int source,
        HullClass hull,
        CapsuleerPopulationPolicy policy,
        IReadOnlySet<int> avoided,
        bool ratting,
        bool roaming,
        ulong draw,
        Func<int, int>? population = null
    )
    {
        var definitions = catalog.Definitions;
        var nearby = Nearby(catalog, source, hull, NullsecExpeditionRules.MaximumJumps, avoided);
        var work = roaming ? NullsecExpeditionWork.Roaming : NullsecExpeditionWork.Ratting;
        (bool MultipleNullSystems, bool MultipleJumps, ulong Rank) best = default;
        int[]? selected = null;
        foreach (var (system, jumps) in nearby.Systems)
        {
            if (jumps < NullsecExpeditionRules.MinimumJumps || !IsNullsec(definitions, system) || !policy.CanWork(definitions, system))
            {
                continue;
            }

            if (!roaming && (!ratting || definitions.BeltAnchors(system).Length == 0))
            {
                continue;
            }
            ulong destinationDraw = TravelerRoutineRules.Mix(draw ^ (uint)system);
            ulong residents = (ulong)(population?.Invoke(system) ?? 0);
            // Give populated destinations the same weight to keep a large hub from dominating the draw.
            ulong divisor = roaming ? (residents == 0 ? QuietRoamingDestinationPenalty : 1UL) : 1UL + residents;
            ulong rank = destinationDraw / divisor;
            bool multipleJumps = jumps > NullsecExpeditionRules.MinimumJumps;
            // A path can only match the winner's first priority once it crosses multiple nullsec systems.
            if (
                best.MultipleNullSystems && (multipleJumps, rank).CompareTo((best.MultipleJumps, best.Rank)) <= 0
                || nearby.PathTo(system, destinationDraw) is not { } systems
            )
            {
                continue;
            }
            var priority = (systems.Skip(1).Count(next => IsNullsec(definitions, next)) >= 2, multipleJumps, rank);
            if (selected is null || priority.CompareTo(best) > 0)
            {
                best = priority;
                selected = systems;
            }
        }
        return selected is null ? null : new(selected, work);
    }

    internal static CapsuleerTrafficRoute? NullsecRefuge(
        CatalogDbContext catalog,
        int source,
        HullClass hull,
        CapsuleerPopulationPolicy policy,
        Predicate<int> canDockInSystem,
        IReadOnlySet<int> avoided,
        ulong draw
    )
    {
        var definitions = catalog.Definitions;
        var graph = definitions.GlobalDefinition(s_graph);
        var nearby = Nearby(catalog, source, hull, TrafficJourneyRules.MaximumJumps, avoided);
        foreach (
            var (system, _) in nearby
                .Systems.Where(stop =>
                    policy.CanWork(definitions, stop.SystemId)
                    && graph.Stations.ContainsKey(stop.SystemId)
                    && canDockInSystem(stop.SystemId)
                )
                .OrderBy(stop => stop.Jumps)
                .ThenBy(stop => TravelerRoutineRules.Mix(draw ^ (uint)stop.SystemId))
        )
        {
            if (nearby.PathTo(system, draw) is { } systems)
            {
                int station = graph.Stations[system].MaxBy(station => TravelerRoutineRules.Mix(draw ^ (uint)station));
                return new(station, station, systems.Reverse().ToArray());
            }
        }
        return null;
    }
}
