using System.Collections.Concurrent;
using System.Diagnostics;
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
    private readonly ConcurrentDictionary<CapsuleerAction, long> _lastSlow = new();
    private readonly ConcurrentQueue<TrafficActionTiming> _slow = new();
    private readonly ConcurrentQueue<TrafficActionTiming> _reportedTrades = new();
    internal IReadOnlyList<TrafficActionTiming> SlowActions => _slow.ToArray();
    internal IReadOnlySet<long>? ReportedTradePilots { get; set; }
    internal IReadOnlyList<TrafficActionTiming> ReportedTrades => _reportedTrades.ToArray();

    internal IDisposable Measure(
        CapsuleerJob job,
        IReadOnlyList<int> systems,
        Func<(long Count, bool Copying)> checkpointCopies,
        Func<TimeSpan?>? ownerReacquire = null
    )
    {
        var before = checkpointCopies();
        var gcPauseAtStart = GC.GetTotalPauseDuration();
        bool reportedTrade = job.Action == CapsuleerAction.Trade && ReportedTradePilots?.Contains(job.PilotId) == true;
        // SQL, fits, allocations and wall time are counted for every action. Explicit tracing,
        // selected trades and repeats of a slow action also capture the individual phase timings.
        bool detail =
            SessionPerformance.IncludesPhases
            || reportedTrade
            || _lastSlow.TryGetValue(job.Action, out long last) && Stopwatch.GetElapsedTime(last) < TimeSpan.FromMinutes(1);
        List<PerformanceSample>? phases = detail ? [] : null;
        return SessionPerformance.Capture(
            "traffic.measuredAction",
            sample =>
            {
                if (sample.Name != "traffic.measuredAction")
                {
                    if (phases is { Count: < RetainedPhases })
                    {
                        phases.Add(sample);
                    }
                    return;
                }
                bool slow = sample.Elapsed.TotalMilliseconds >= SlowActionMilliseconds;
                if (!slow && !reportedTrade)
                {
                    return;
                }
                var timing = new TrafficActionTiming(
                    job,
                    systems,
                    before.Copying || checkpointCopies().Count != before.Count,
                    sample,
                    phases?.AsReadOnly() ?? (IReadOnlyList<PerformanceSample>)[]
                );
                if (reportedTrade)
                {
                    Retain(_reportedTrades, timing);
                }
                if (!slow)
                {
                    return;
                }
                double gcPause = (GC.GetTotalPauseDuration() - gcPauseAtStart).TotalMilliseconds;
                _lastSlow[job.Action] = Stopwatch.GetTimestamp();
                Retain(_slow, timing);
                string breakdown = string.Join(
                    ", ",
                    timing
                        .Phases.Where(p =>
                            p.Name is not ("traffic.queueExecution" or "traffic.haulerAction" or "traffic.minerAction")
                            && !p.Name.StartsWith("traffic.action.", StringComparison.Ordinal)
                        )
                        .OrderByDescending(p => p.Elapsed)
                        .Take(4)
                        .Select(p => $"{p.Name}={p.Elapsed.TotalMilliseconds:F2} ms (SQL={p.Commands}, connections={p.Connections})")
                );
                string wait = ownerReacquire?.Invoke() is { } elapsed
                    ? $"ownerReacquire={elapsed.TotalMilliseconds:F2} ms, "
                    : string.Empty;
                ServerLog.Console.Warning(
                    $"Slow traffic action {job.Action} pilot={job.PilotId}: "
                        + $"total={sample.Elapsed.TotalMilliseconds:F2} ms, SQL={sample.Commands}, connections={sample.Connections}, "
                        + $"fits={sample.Fittings}, allocated={sample.AllocatedBytes}, "
                        + $"{wait}gcPause={gcPause:F2} ms, checkpointOverlap={timing.CheckpointOverlap}, "
                        + $"sqlElapsed={sample.SqlMilliseconds:F2} ms. {breakdown}"
                );
            },
            includePhases: detail
        );
    }

    private static void Retain(ConcurrentQueue<TrafficActionTiming> timings, TrafficActionTiming timing)
    {
        timings.Enqueue(timing);
        while (timings.Count > RetainedActions)
        {
            timings.TryDequeue(out _);
        }
    }
}
