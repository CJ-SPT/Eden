using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private const int LatencyWarmupSeconds = 30;
    private const int MaximumVisitPhases = 64;
    private static readonly HashSet<long> s_slowTradePilots = [90000016, 90001841, 90007109];

    // Pilots identified in the maintainer's November 10 traffic-owner capture.
    private static readonly long[] LatencyReportedPilots =
    [
        90006929,
        90021899,
        90021901,
        90023100,
        90017420,
        90022028,
        90022979,
        90023258,
        90017498,
        90023156,
        90002848,
        90010900,
        90010802,
        90011046,
        90021914,
    ];

    private sealed record ThroughputRequest(int ArrivalSecond, double Queue, double Total, ReplaySample Result);

    private sealed record ThroughputVisit(int System, PerformanceSample Total, IReadOnlyList<PerformanceSample> Phases);

    private static double ThroughputPercentile(IEnumerable<double> values, double fraction)
    {
        var ordered = values.Order().ToArray();
        return ordered.Length == 0 ? 0 : ordered[Math.Max(0, (int)Math.Ceiling(fraction * ordered.Length) - 1)];
    }

    [Fact]
    public void SustainedPercentilesSortArrivalOrder()
    {
        double[] arrivals = Enumerable.Range(1, 100).Reverse().Select(value => (double)value).ToArray();
        Assert.Equal(95, ThroughputPercentile(arrivals, 0.95));
        Assert.Equal(99, ThroughputPercentile(arrivals, 0.99));
    }

    private sealed class ThroughputClock : TimeProvider
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private static readonly DateTimeOffset StartTime = new(2026, 11, 10, 18, 0, 0, TimeSpan.Zero);
        internal TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => StartTime + Stopwatch.GetElapsedTime(_started) + Offset;

        public override long GetTimestamp() => Stopwatch.GetTimestamp() + (long)(Offset.TotalSeconds * Stopwatch.Frequency);

        public override long TimestampFrequency => Stopwatch.Frequency;
    }

    [Theory]
    [InlineData(450, false)]
    [InlineData(450, true)]
    [InlineData(900, false)]
    [InlineData(900, true)]
    public async Task ReplaySustainedBotThroughput(int systems, bool backlog)
    {
        if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_SOURCE") is not { } source)
        {
            output.WriteLine("Set EDEN_BOT_REPLAY_SOURCE to a frozen saved-world directory to run sustained throughput.");
            return;
        }
        if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_CASE") is { } selected && selected != $"{systems}-{backlog}")
        {
            return;
        }
        systems = int.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_SYSTEMS") ?? systems.ToString());
        int observers = int.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_OBSERVERS") ?? "1");
        int seconds = int.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_SECONDS") ?? "600");
        string destination =
            Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT")
            ?? throw new InvalidOperationException("Set EDEN_PERFORMANCE_OUTPUT to retain throughput results.");
        Directory.CreateDirectory(destination);
        string name = $"throughput-{systems}-{backlog}-{observers}";
        string progress = Path.Combine(destination, name + ".progress");
        File.WriteAllText(progress, "Copying frozen source.\n");
        Stopwatch setup = Stopwatch.StartNew();
        string directory = CopySnapshot(source);
        var reports = Channel.CreateUnbounded<string>(new() { SingleReader = true });
        var writingLog = WriteThroughputLog(Path.Combine(destination, name + ".log"), reports.Reader);
        void Report(string line) => reports.Writer.TryWrite(line);
        ConcurrentQueue<OwnershipTiming> ownership = new();
        using var logging = ServerLog.Console.Redirect(
            (level, message) =>
            {
                if (level is ServerLogLevel.Warning or ServerLogLevel.Error)
                {
                    Report(message);
                }
            }
        );
        List<object> samples = [];
        ConcurrentDictionary<string, (int Count, string Stack)> sqlCalls = new();
        try
        {
            string traffic = Path.Combine(source, "traffic.sqlite");
            if (File.Exists(traffic))
            {
                File.Copy(traffic, Path.Combine(directory, "traffic.sqlite"));
            }
            foreach (string file in new[] { "world.sqlite.skills.sqlite", "world.sqlite.skills-active" })
            {
                if (File.Exists(Path.Combine(source, file)))
                {
                    File.Copy(Path.Combine(source, file), Path.Combine(directory, file));
                }
            }
            string packages = Path.Combine(source, "world.sqlite.skill-packages");
            if (Directory.Exists(packages))
            {
                foreach (string file in Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(directory, Path.GetRelativePath(source, file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target);
                }
            }
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            ThroughputClock clock = new();
            File.AppendAllText(progress, "Initializing copied world.\n");
            WorldInitialization.Initialize(database, clock);
            int[] communities;
            using (WorldDbContext world = new(database.Path))
            {
                var reportedCommunities = world
                    .Set<SavedCapsuleerAgent>()
                    .Where(agent => LatencyReportedPilots.Contains(agent.PilotId))
                    .Select(agent => agent.CommunitySystemId)
                    .Distinct()
                    .ToHashSet();
                communities = world
                    .Set<SavedCapsuleerAgent>()
                    .GroupBy(agent => agent.CommunitySystemId)
                    .Select(group => new { System = group.Key, Pilots = group.Count() })
                    .ToArray()
                    .OrderByDescending(group => reportedCommunities.Contains(group.System))
                    .ThenBy(group => Math.Abs(group.Pilots - 14))
                    .ThenBy(group => group.System)
                    .Take(systems)
                    .Select(group => group.System)
                    .ToArray();
            }
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            using var player = new ReplayPlayer(host, clock, 90000006, 1000003, Report);
            using var additional = new ThroughputObservers(host, clock, observers - 1, player.Context.Pilot.SystemId, Report);
            ReplayPlayer[] players = [player, .. additional.Players];
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var diagnostics = host.GetRequiredService<ICapsuleerDiagnostics>();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            live.MeasureBotReplay = true;
            live.TrafficActions.ReportedTradePilots = s_slowTradePilots;
            using var ownershipObservation = live.ObserveOwnership(timing =>
            {
                if (timing.Hold.TotalMilliseconds >= 25 || timing.Wait.TotalMilliseconds >= 25)
                {
                    ownership.Enqueue(timing);
                }
            });
            List<ThroughputVisit> visits = [];
            var exploration = Stopwatch.StartNew();
            long beforeExploration = GC.GetAllocatedBytesForCurrentThread();
            using (var explorer = host.GetRequiredService<IHumanPresence>().Select(90000001, communities[0]))
            {
                foreach (int system in communities)
                {
                    PriorityQueue<PerformanceSample, long> phases = new();
                    using var visit =
                        Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_ENTRY_TRACE") != "1"
                            ? null
                            : SessionPerformance.Capture(
                                "exploration visit",
                                sample =>
                                {
                                    if (sample.Name == "exploration visit")
                                    {
                                        if (sample.Elapsed.TotalMilliseconds >= 100)
                                        {
                                            visits.Add(
                                                new(
                                                    system,
                                                    sample,
                                                    phases
                                                        .UnorderedItems.Select(item => item.Element)
                                                        .OrderByDescending(p => p.Elapsed)
                                                        .ToArray()
                                                )
                                            );
                                        }
                                    }
                                    else if (phases.Count < MaximumVisitPhases)
                                    {
                                        phases.Enqueue(sample, sample.Elapsed.Ticks);
                                    }
                                    else
                                    {
                                        phases.EnqueueDequeue(sample, sample.Elapsed.Ticks);
                                    }
                                }
                            );
                    explorer.Transfer(system);
                    if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_PREPARATION") == "1")
                    {
                        host.GetRequiredService<ICapsuleerBackgroundPreparation>().PrepareNext();
                    }
                    File.AppendAllText(progress, $"Activated {system}.\n");
                }
            }
            long explorationBytes = GC.GetAllocatedBytesForCurrentThread() - beforeExploration;
            exploration.Stop();
            ReplaySample[] cold = [await player.Frame(), await player.Call("marketProxy", "GetRegionBest")];
            foreach (var observer in additional.Players)
            {
                await observer.Frame();
                await observer.Call("marketProxy", "GetRegionBest");
            }
            setup.Stop();
            if (backlog)
            {
                clock.Offset = TimeSpan.FromSeconds(70);
            }
            bool traceWorker = Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_WORKER_TRACE") != "0";
            using var metrics = Eden.Infrastructure.Diagnostics.SessionPerformance.Begin(
                "sustained bot worker",
                Report,
                force: traceWorker,
                command: Environment.GetEnvironmentVariable("EDEN_SQL_HOTSPOTS") == "1"
                    ? sql => sqlCalls.AddOrUpdate(sql, key => (1, Environment.StackTrace), (_, value) => (value.Count + 1, value.Stack))
                    : null,
                slowCommand: (duration, sql) => Report($"Slow SQL command: {duration.TotalMilliseconds:F2} ms. {sql}")
            );
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            long completedAtStart = diagnostics.Read().CompletedActions;
            int generation2AtStart = GC.CollectionCount(2);
            maintenance.Start();
            var marketMaintenance =
                Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_MARKET_MAINTENANCE") == "1"
                    ? host.GetRequiredService<MarketMaintenance>()
                    : null;
            marketMaintenance?.Start();
            var populationMaintenance = host.GetRequiredService<CapsuleerPopulationMaintenance>();
            if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_PREPARATION") == "1")
            {
                populationMaintenance.Start();
            }
            TimeSpan startedCpu = Process.GetCurrentProcess().TotalProcessorTime;
            // The approximate counter can stay flat for several samples while bots continue to work.
            long startedAllocations = GC.GetTotalAllocatedBytes(precise: true);
            long timedAllocations = 0;
            bool allocationSamplesMonotonic = true;
            long arrivalsStarted = Stopwatch.GetTimestamp();
            Stopwatch elapsed = Stopwatch.StartNew();
            ConcurrentQueue<ThroughputRequest> requests = new();
            List<Task> pendingRequests = [];
            SemaphoreSlim[] sessions = players.Select(_ => new SemaphoreSlim(1)).ToArray();
            var arrivals = Task.Run(async () =>
            {
                for (int second = 1; second <= seconds; second++)
                {
                    double delay = second * 1000 - elapsed.Elapsed.TotalMilliseconds;
                    if (delay > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(delay));
                    }
                    int arrivalSecond = second;
                    // Include producer/thread-pool lateness as well as the session's own queue.
                    long arrived = arrivalsStarted + (long)second * Stopwatch.Frequency;
                    bool market = second % 3 == 0;
                    foreach (var (observer, session) in players.Zip(sessions))
                    {
                        pendingRequests.Add(
                            Task.Run(async () =>
                            {
                                await session.WaitAsync();
                                double queue = Stopwatch.GetElapsedTime(arrived).TotalMilliseconds;
                                try
                                {
                                    var result = market ? await observer.Call("marketProxy", "GetRegionBest") : await observer.Frame();
                                    requests.Enqueue(
                                        new(arrivalSecond, queue, Stopwatch.GetElapsedTime(arrived).TotalMilliseconds, result)
                                    );
                                }
                                finally
                                {
                                    session.Release();
                                }
                            })
                        );
                    }
                }
                await Task.WhenAll(pendingRequests);
            });
            ConcurrentQueue<WorldCheckpoint> checkpointReports = [];
            var checkpoints = Task.Run(async () =>
            {
                for (int second = 15; second <= seconds; second += 15)
                {
                    double delay = second * 1000 - elapsed.Elapsed.TotalMilliseconds;
                    if (delay > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(delay));
                    }
                    await live.FlushAsync();
                    if (live.LastCheckpoint is { } checkpoint)
                    {
                        checkpointReports.Enqueue(checkpoint);
                    }
                }
            });
            int minimumScheduled = int.MaxValue;
            double? recovered = null;
            long previousCompleted = 0;
            int previousDue = diagnostics.Read().DueJobs;
            double previousSeconds = 0;
            IReadOnlyList<CapsuleerCareerQueue> previousCareers = [];
            TimeSpan waitAtStart = maintenance.WorkerWait;
            TimeSpan pauseAtStart = GC.GetTotalPauseDuration();
            List<double> finalDelays = [];
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                var state = diagnostics.Read();
                minimumScheduled = Math.Min(minimumScheduled, state.ScheduledAgents);
                var blockingCollection = GC.GetGCMemoryInfo(GCKind.FullBlocking);
                var backgroundCollection = GC.GetGCMemoryInfo(GCKind.Background);
                var fullCollection = blockingCollection.Index > backgroundCollection.Index ? blockingCollection : backgroundCollection;
                long sampleAllocations = GC.GetTotalAllocatedBytes(precise: true) - startedAllocations;
                allocationSamplesMonotonic &= sampleAllocations >= timedAllocations;
                timedAllocations = sampleAllocations;
                double delay = TimeSpan.FromTicks(state.OldestDelayTicks).TotalMilliseconds;
                if (delay < 1000)
                {
                    recovered ??= elapsed.Elapsed.TotalSeconds;
                }
                if (elapsed.Elapsed.TotalSeconds >= seconds - Math.Min(300, seconds / 2))
                {
                    finalDelays.Add(delay);
                }
                samples.Add(
                    new
                    {
                        Seconds = elapsed.Elapsed.TotalSeconds,
                        AllocatedBytes = timedAllocations,
                        state.ScheduledAgents,
                        state.DueJobs,
                        OldestMilliseconds = delay,
                        Completed = state.CompletedActions,
                        ActionsSinceSample = state.CompletedActions - previousCompleted,
                        ActionsPerSecond = (state.CompletedActions - previousCompleted)
                            / Math.Max(0.001, elapsed.Elapsed.TotalSeconds - previousSeconds),
                        EstimatedDueArrivalsPerSecond = (state.CompletedActions - previousCompleted + state.DueJobs - previousDue)
                            / Math.Max(0.001, elapsed.Elapsed.TotalSeconds - previousSeconds),
                        state.Failures,
                        CareerQueue = state
                            .Careers.Select(career =>
                            {
                                var previous = previousCareers.FirstOrDefault(entry => entry.Career == career.Career);
                                return new
                                {
                                    career.Career,
                                    career.Due,
                                    career.Completed,
                                    EstimatedNewlyDue = previous is null
                                        ? 0
                                        : career.Completed - previous.Completed + career.Due - previous.Due,
                                };
                            })
                            .ToArray(),
                        WorkerWaitMilliseconds = (maintenance.WorkerWait - waitAtStart).TotalMilliseconds,
                        Generation2Collections = GC.CollectionCount(2) - generation2AtStart,
                        GcPauseMilliseconds = (GC.GetTotalPauseDuration() - pauseAtStart).TotalMilliseconds,
                        LastFullCollectionIndex = fullCollection.Index,
                        BytesAfterFullCollection = fullCollection.HeapSizeBytes - fullCollection.FragmentedBytes,
                        Runtime = state,
                    }
                );
                previousCompleted = state.CompletedActions;
                previousDue = state.DueJobs;
                previousSeconds = elapsed.Elapsed.TotalSeconds;
                previousCareers = state.Careers;
                File.AppendAllText(
                    progress,
                    $"seconds={elapsed.Elapsed.TotalSeconds:F1} scheduled={state.ScheduledAgents} "
                        + $"due={state.DueJobs} delayMs={delay:F1} completed={state.CompletedActions}\n"
                );
                Assert.Equal(0, state.Failures);
            }
            await arrivals;
            foreach (var session in sessions)
            {
                session.Dispose();
            }
            await checkpoints;
            maintenance.Dispose();
            if (marketMaintenance is not null)
            {
                await marketMaintenance.DisposeAsync();
                Assert.Null(marketMaintenance.LastFailure);
            }
            await live.FlushAsync();
            if (live.LastCheckpoint is { } finalCheckpoint)
            {
                checkpointReports.Enqueue(finalCheckpoint);
            }

            File.WriteAllText(
                Path.Combine(destination, name + ".json"),
                JsonSerializer.Serialize(
                    new
                    {
                        systems,
                        observers,
                        backlog,
                        seconds,
                        RecoveredSeconds = recovered,
                        SetupMilliseconds = setup.Elapsed.TotalMilliseconds,
                        ExplorationMilliseconds = exploration.Elapsed.TotalMilliseconds,
                        ExplorationAllocatedBytes = explorationBytes,
                        ColdSamples = cold,
                        Visits = visits,
                        Checkpoints = checkpointReports.DistinctBy(checkpoint => checkpoint.CompletedAt).ToArray(),
                        NpcPopulation = live.NpcPopulationCounts.ToArray(),
                        MeasurementTimestamp = arrivalsStarted,
                        TimestampFrequency = Stopwatch.Frequency,
                        ServerGC = System.Runtime.GCSettings.IsServerGC,
                        TraceWorkerPhases = traceWorker,
                        MarketMaintenance = marketMaintenance is not null,
                        MinimumScheduled = minimumScheduled,
                        scheduler.TradesStartedAhead,
                        scheduler.TradesUsedAhead,
                        scheduler.RoutineArrivals,
                        scheduler.PreparedArrivals,
                        scheduler.EarlyMiningChecks,
                        scheduler.QuietMiningChecks,
                        scheduler.PaidCollections,
                        ReportedTrades = live.TrafficActions.ReportedTrades,
                        Samples = samples,
                        Requests = requests,
                        Ownership = ownership,
                        WarmupSeconds = LatencyWarmupSeconds,
                        ReportedPilots = LatencyReportedPilots,
                        Communities = communities,
                        CpuMilliseconds = (Process.GetCurrentProcess().TotalProcessorTime - startedCpu).TotalMilliseconds,
                        AllocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - startedAllocations,
                        AllocationSamplesMonotonic = allocationSamplesMonotonic,
                        BytesPerCompletedAction = allocationSamplesMonotonic
                            ? timedAllocations / (double)Math.Max(1, previousCompleted - completedAtStart)
                            : (double?)null,
                    }
                )
            );
            File.WriteAllText(
                Path.Combine(destination, name + ".sql.json"),
                JsonSerializer.Serialize(
                    sqlCalls
                        .OrderByDescending(p => p.Value.Count)
                        .Take(30)
                        .Select(p => new
                        {
                            Sql = p.Key,
                            p.Value.Count,
                            p.Value.Stack,
                        })
                )
            );
            File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(destination, name + ".manifest.json"), overwrite: true);
            output.WriteLine($"Systems={systems} backlog={backlog} recovered={recovered} scheduled={diagnostics.Read().ScheduledAgents}");
            if (seconds >= 600 && Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_BASELINE") != "1")
            {
                if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_MINIMUM_PILOTS") is { } required)
                {
                    Assert.True(minimumScheduled >= int.Parse(required), $"Minimum scheduled population was {minimumScheduled}.");
                }
                Assert.NotNull(recovered);
                Assert.True(recovered <= 120);
                Assert.All(finalDelays, delay => Assert.True(delay < 1000));
                var warmed = requests.Where(request => request.ArrivalSecond > LatencyWarmupSeconds).ToArray();
                Assert.All(warmed, request => Assert.True(request.Total < 1000, $"{request.Result.Operation}: {request.Total:F2} ms"));
                Assert.All(
                    warmed,
                    request => Assert.Equal((0, 0, 0), (request.Result.Sql, request.Result.Connections, request.Result.Fits))
                );
                foreach (string operation in new[] { "frame", "GetRegionBest" })
                {
                    var values = warmed.Where(request => request.Result.Operation == operation).Select(request => request.Total).ToArray();
                    Assert.NotEmpty(values);
                    Assert.True(ThroughputPercentile(values, 0.95) < 100, $"{operation} p95={ThroughputPercentile(values, 0.95):F2} ms");
                    Assert.True(ThroughputPercentile(values, 0.99) < 250, $"{operation} p99={ThroughputPercentile(values, 0.99):F2} ms");
                }
                Assert.DoesNotContain(
                    ownership,
                    timing =>
                        (
                            timing.Operation.Contains("traffic", StringComparison.Ordinal)
                            || timing.Operation.Contains("population", StringComparison.Ordinal)
                        )
                        && timing.Hold.TotalMilliseconds >= 250
                );
            }
        }
        finally
        {
            reports.Writer.Complete();
            try
            {
                await writingLog;
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task WriteThroughputLog(string path, ChannelReader<string> reports)
    {
        const int bufferSize = 128 * 1024;
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize, useAsync: true);
        await using StreamWriter log = new(stream, bufferSize: bufferSize);
        await foreach (string line in reports.ReadAllAsync())
        {
            await log.WriteLineAsync(line);
        }
    }

    private sealed class ThroughputObservers : IDisposable
    {
        internal List<ReplayPlayer> Players { get; } = [];

        internal ThroughputObservers(ServiceProvider host, TimeProvider clock, int count, int system, Action<string> report)
        {
            (long Pilot, long Account)[] identities = [(90000001, 1000001), (90000002, 1000002), (90000004, 1000004)];
            var ships = host.GetRequiredService<Eden.World.Docking.SqliteWorld>();
            foreach (var identity in identities.Take(count))
            {
                var pilot = ships.FindPilot(identity.Account, identity.Pilot)!;
                var reference = ships.FindPilot(1000003, 90000006)!;
                ships.SetPosition(pilot.ShipId, new(reference.X + 2000000 + Players.Count * 100000, reference.Y, reference.Z), system);
                Players.Add(new(host, clock, identity.Pilot, identity.Account, report));
            }
        }

        public void Dispose()
        {
            foreach (var player in Players)
            {
                player.Dispose();
            }
        }
    }
}
