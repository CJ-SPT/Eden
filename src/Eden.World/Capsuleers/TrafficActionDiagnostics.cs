using System.Collections.Concurrent;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;

namespace Eden.World.Capsuleers;

internal sealed record TrafficActionTiming(
    CapsuleerJob Job,
    IReadOnlyList<int> Systems,
    bool CheckpointOverlap,
    PerformanceSample Total,
    IReadOnlyList<PerformanceSample> Phases
);

internal sealed class TrafficActionDiagnostics
{
    private const int RetainedActions = 128;
    private const int RetainedPhases = 64;
    private const int SlowActionMilliseconds = 250;
    private readonly ConcurrentQueue<TrafficActionTiming> _slow = new();
    internal IReadOnlyList<TrafficActionTiming> SlowActions => _slow.ToArray();

    internal IDisposable Measure(CapsuleerJob job, IReadOnlyList<int> systems, Func<(long Count, bool Copying)> checkpointCopies)
    {
        var before = checkpointCopies();
        List<PerformanceSample> phases = [];
        return SessionPerformance.Capture(
            "traffic.measuredAction",
            sample =>
            {
                if (sample.Name != "traffic.measuredAction")
                {
                    if (phases.Count < RetainedPhases)
                    {
                        phases.Add(sample);
                    }
                    return;
                }
                if (sample.Elapsed.TotalMilliseconds < SlowActionMilliseconds)
                {
                    return;
                }
                var timing = new TrafficActionTiming(
                    job,
                    systems,
                    before.Copying || checkpointCopies().Count != before.Count,
                    sample,
                    phases.AsReadOnly()
                );
                _slow.Enqueue(timing);
                while (_slow.Count > RetainedActions)
                {
                    _slow.TryDequeue(out _);
                }
                string detail = string.Join(
                    ", ",
                    phases
                        .Where(p =>
                            p.Name
                                is not (
                                    "traffic.queueExecution"
                                    or "traffic.haulerAction"
                                    or "traffic.minerAction"
                                    or "traffic.tradeAndLaunch"
                                )
                            && !p.Name.StartsWith("traffic.action.", StringComparison.Ordinal)
                        )
                        .OrderByDescending(p => p.Elapsed)
                        .Take(4)
                        .Select(p => $"{p.Name}={p.Elapsed.TotalMilliseconds:F2} ms (SQL={p.Commands}, connections={p.Connections})")
                );
                ServerLog.Console.Warning(
                    $"Slow traffic action {job.Action} pilot={job.PilotId}: "
                        + $"total={sample.Elapsed.TotalMilliseconds:F2} ms, SQL={sample.Commands}, connections={sample.Connections}, "
                        + $"fits={sample.Fittings}, allocated={sample.AllocatedBytes}, "
                        + $"checkpointOverlap={timing.CheckpointOverlap}. {detail}"
                );
            }
        );
    }
}
