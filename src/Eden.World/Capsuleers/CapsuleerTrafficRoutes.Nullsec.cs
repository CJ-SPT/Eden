using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;

namespace Eden.World.Capsuleers;

internal sealed record NullsecDestination(int[] Systems, NullsecExpeditionWork Work);

internal static partial class CapsuleerTrafficRoutes
{
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
        var preferredWork = roaming && (!ratting || (draw & 1) == 0) ? NullsecExpeditionWork.Roaming : NullsecExpeditionWork.Ratting;
        (bool MultipleNullSystems, bool MultipleJumps, bool PreferredWork, ulong Rank) best = default;
        NullsecDestination? result = null;
        foreach (var (system, jumps) in nearby.Systems)
        {
            if (jumps < NullsecExpeditionRules.MinimumJumps || !IsNullsec(definitions, system) || !policy.CanWork(definitions, system))
            {
                continue;
            }

            bool canRat = ratting && definitions.BeltAnchors(system).Length > 0;
            if (!canRat && !roaming)
            {
                continue;
            }
            ulong destinationDraw = TravelerRoutineRules.Mix(draw ^ (uint)system);
            if (nearby.PathTo(system, destinationDraw) is not { } systems)
            {
                continue;
            }
            var work =
                canRat && (preferredWork == NullsecExpeditionWork.Ratting || !roaming)
                    ? NullsecExpeditionWork.Ratting
                    : NullsecExpeditionWork.Roaming;
            ulong rank = destinationDraw / (1UL + (ulong)(population?.Invoke(system) ?? 0));
            var priority = (
                systems.Skip(1).Count(next => IsNullsec(definitions, next)) >= 2,
                jumps > NullsecExpeditionRules.MinimumJumps,
                work == preferredWork,
                rank
            );
            if (result is null || priority.CompareTo(best) > 0)
            {
                best = priority;
                result = new(systems, work);
            }
        }
        return result;
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
