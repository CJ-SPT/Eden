using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime;

namespace Eden.Server.Administration;

public sealed record ServerResourceSample(
    TimeSpan Uptime,
    TimeSpan ProcessorTime,
    int ProcessorCount,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    long ManagedMemoryBytes,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ProcessThreads,
    int ThreadPoolThreads,
    long PendingWorkItems
);

public sealed record ServerCheckpointPerformance(
    long Generation,
    DateTimeOffset CompletedAt,
    TimeSpan Wait,
    TimeSpan Capture,
    TimeSpan Projection,
    TimeSpan Write,
    long Bytes
);

public sealed record ServerCapsuleerPerformance(CapsuleerRuntimeSummary Runtime, int MaterializedPilots);

public sealed record ServerPerformanceSnapshot(
    DateTimeOffset SampledAt,
    ServerResourceSample Resources,
    double? CpuPercent,
    double? AllocatedBytesPerSecond,
    int ActiveSessions,
    bool ResidentWorld,
    ServerCheckpointPerformance? LastCheckpoint,
    bool CheckpointFailed
)
{
    public ServerCapsuleerPerformance? Capsuleers { get; init; }
}

// One shared, demand-driven sample bounds overhead across administrator circuits.
// Reading diagnostics never enters the world owner, projects combat or opens SQLite.
internal sealed class ServerPerformanceMonitor(
    TimeProvider clock,
    PilotGate pilots,
    LiveWorld? world = null,
    Func<ServerResourceSample>? readResources = null,
    Func<ServerCapsuleerPerformance>? readCapsuleers = null
)
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);
    private readonly object _gate = new();
    private ServerPerformanceSnapshot? _latest;
    private long _sampleTimestamp;

    internal ServerPerformanceSnapshot Read()
    {
        lock (_gate)
        {
            long timestamp = clock.GetTimestamp();
            TimeSpan elapsed = clock.GetElapsedTime(_sampleTimestamp, timestamp);
            if (_latest is not null && elapsed < SampleInterval)
            {
                return _latest;
            }
            var sample = (readResources ?? ReadResources)();
            double? cpu = null;
            double? allocations = null;
            if (_latest is { Resources: var previous })
            {
                // Normalize process CPU time to all processors available to this process (0–100%).
                cpu = Math.Clamp(
                    (sample.ProcessorTime - previous.ProcessorTime).TotalSeconds / elapsed.TotalSeconds / sample.ProcessorCount * 100,
                    0,
                    100
                );
                allocations = Math.Max(0, sample.AllocatedBytes - previous.AllocatedBytes) / elapsed.TotalSeconds;
            }
            _sampleTimestamp = timestamp;
            var checkpoint = world?.LastCheckpoint;
            _latest = new(
                clock.GetUtcNow(),
                sample,
                cpu,
                allocations,
                pilots.ActiveSessions,
                world is not null,
                checkpoint is null
                    ? null
                    : new(
                        checkpoint.Generation,
                        checkpoint.CompletedAt,
                        checkpoint.Wait,
                        checkpoint.Capture,
                        checkpoint.Projection,
                        checkpoint.Write,
                        checkpoint.Bytes
                    ),
                world?.LastSaveFailure is not null
            )
            {
                Capsuleers = readCapsuleers?.Invoke(),
            };
            return _latest;
        }
    }

    private static ServerResourceSample ReadResources()
    {
        using var process = Process.GetCurrentProcess();
        return new(
            DateTime.UtcNow - process.StartTime.ToUniversalTime(),
            process.TotalProcessorTime,
            Environment.ProcessorCount,
            process.WorkingSet64,
            process.PrivateMemorySize64,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.GetTotalAllocatedBytes(precise: false),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            process.Threads.Count,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount
        );
    }
}
