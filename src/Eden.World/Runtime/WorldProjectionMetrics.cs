using Eden.Infrastructure.Diagnostics;

namespace Eden.World.Runtime;

internal sealed record WorldTableProjection(string Table, int Rows, int Commands, long AllocatedBytes, TimeSpan Elapsed);

internal sealed class WorldProjectionMetrics
{
    private readonly List<WorldTableProjection> _tables = [];
    internal IReadOnlyList<WorldTableProjection> Tables => _tables;

    internal IDisposable Measure(string table, int rows) =>
        SessionPerformance.Capture(
            $"checkpoint.project.{table}",
            sample =>
            {
                if (sample.Name == $"checkpoint.project.{table}")
                {
                    _tables.Add(new(table, rows, sample.Commands, sample.AllocatedBytes, sample.Elapsed));
                }
            }
        );
}
