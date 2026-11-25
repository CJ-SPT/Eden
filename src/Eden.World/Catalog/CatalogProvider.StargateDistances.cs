using System.Buffers;

namespace Eden.World.Catalog;

public sealed partial class CatalogProvider
{
    private sealed record StargateGraph(IReadOnlyDictionary<int, int> Systems, int[][] Edges);

    private static readonly CatalogDefinition<StargateGraph> s_stargateGraph = new(
        "stargates.graph",
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

    private static readonly CatalogDefinition<int[]> s_stargateDistances = new(
        "stargates.distances",
        (catalog, source) =>
        {
            var graph = catalog.Definitions.GlobalDefinition(s_stargateGraph);
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

    internal int? StargateDistance(int source, int target)
    {
        if (source == target)
        {
            return 0;
        }
        var graph = GlobalDefinition(s_stargateGraph);
        if (!graph.Systems.ContainsKey(source) || !graph.Systems.TryGetValue(target, out int destination))
        {
            return null;
        }
        int distance = Definition(s_stargateDistances, source)[destination];
        return distance >= 0 ? distance : null;
    }
}
