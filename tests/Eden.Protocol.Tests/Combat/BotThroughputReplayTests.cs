using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Eden.Data.World.Capsuleers;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Configuration;
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

    private sealed record ThroughputJobProgress(int Tracked, int Advanced, int UnchangedDue, int UnchangedFuture, int Missing);

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

    private sealed class ThroughputClock(DateTimeOffset startTime) : TimeProvider
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        internal TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => startTime + Stopwatch.GetElapsedTime(_started) + Offset;

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
        int? requiredPilots = Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_MINIMUM_PILOTS") is { } minimum
            ? int.Parse(minimum)
            : null;
        int admissionTimeout = int.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_ADMISSION_SECONDS") ?? "0");
        long observerPilot = long.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_OBSERVER_PILOT") ?? "90000006");
        long observerAccount = long.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_OBSERVER_ACCOUNT") ?? "1000003");
        var startTime = Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_START_UTC") is { } start
            ? DateTimeOffset.Parse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
            : new DateTimeOffset(2026, 11, 10, 18, 0, 0, TimeSpan.Zero);
        string destination =
            Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT")
            ?? throw new InvalidOperationException("Set EDEN_PERFORMANCE_OUTPUT to retain throughput results.");
        Directory.CreateDirectory(destination);
        string name = $"throughput-{systems}-{backlog}-{observers}";
        string progress = Path.Combine(destination, name + ".progress");
        File.WriteAllText(progress, "Copying frozen source.\n");
        Stopwatch setup = Stopwatch.StartNew();
        FrozenReplayCatalogs? immutableCatalogs =
            Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_IMMUTABLE_CATALOG_LINKS") == "1" ? new(source) : null;
        string directory = CopySnapshot(source, immutableCatalogs);
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
            CopyThroughputSidecars(source, directory, immutableCatalogs);
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            ThroughputClock clock = new(startTime);
            File.AppendAllText(progress, "Initializing copied world.\n");
            WorldInitialization.Initialize(database, clock);
            if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_ACTIVE_PILOT_CAP") is { } cap)
            {
                int activePilotCap = int.Parse(cap, CultureInfo.InvariantCulture);
                WorldConfigurationStore store = new(database, clock);
                var state = store.Read();
                var draft = state.Active.Draft;
                var settings = draft.Settings with { Capsuleers = draft.Settings.Capsuleers with { ActivePilotCap = activePilotCap } };
                store.Save(state.Generation, draft with { Settings = settings }, "Throughput replay");
                WorldConfigurationStore.Activate(database, clock);
                CapsuleerPopulationActivation.Apply(database, clock);
                Assert.Equal(activePilotCap, database.Settings.Capsuleers.ActivePilotCap);
                File.AppendAllText(progress, $"Set copied world's active pilot cap to {activePilotCap}.\n");
            }
            int[] communities;
            int[] explorationSystems;
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
                explorationSystems = communities;
                if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_RESUME_OCCUPIED") == "1")
                {
                    var outsideCommunities = SavedCapsuleerSystems(database, world, communities).Except(communities).Order().ToArray();
                    explorationSystems = [.. communities, .. outsideCommunities];
                    File.AppendAllText(progress, $"Additional saved ship systems to visit: {string.Join(", ", outsideCommunities)}.\n");
                }
            }
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            using var player = new ReplayPlayer(host, clock, observerPilot, observerAccount, Report);
            using var additional = new ThroughputObservers(host, clock, observers - 1, player.Context.Pilot, Report);
            ReplayPlayer[] players = [player, .. additional.Players];
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var diagnostics = host.GetRequiredService<ICapsuleerDiagnostics>();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            live.MeasureBotReplay = true;
            live.TrafficActions.ReportedTradePilots = s_slowTradePilots;
            if (Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_PREFILL_ROSTER") == "1")
            {
                Assert.True(database.Settings.FreshUniverse);
                Assert.True(requiredPilots is > 0);
                await PrefillThroughputRoster(
                    host.GetRequiredService<ICapsuleerBackgroundPreparation>(),
                    live,
                    requiredPilots.Value,
                    admissionTimeout,
                    progress
                );
                await live.FlushAsync();
            }
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
                foreach (int system in explorationSystems)
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
            maintenance.Start();
            var activityRewards = host.GetRequiredService<ActivityRewardMaintenance>();
            activityRewards.Start();
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
            var admission = Stopwatch.StartNew();
            if (admissionTimeout > 0 && requiredPilots is { } admissionTarget)
            {
                await WaitForThroughputPopulation(diagnostics, live, admissionTarget, admissionTimeout, progress);
            }
            admission.Stop();
            Dictionary<long, long>? startingSequences = null;
            if (requiredPilots is not null)
            {
                using var owner = live.EnterPersistence("replay job progress start");
                startingSequences = live.ProjectedCapsuleerJobs.Values.ToDictionary(job => job.PilotId, job => job.Sequence);
                foreach (var job in live.CapsuleerJobs.Values)
                {
                    startingSequences[job.PilotId] = job.Sequence;
                }
            }
            long completedAtStart = diagnostics.Read().CompletedActions;
            long paidAtStart = scheduler.PaidCollections;
            int generation2AtStart = GC.CollectionCount(2);
            using var process = Process.GetCurrentProcess();
            TimeSpan startedCpu = process.TotalProcessorTime;
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
            bool nativeCheckpoints = Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_NATIVE_CHECKPOINTS") == "1";
            var checkpoints = nativeCheckpoints
                ? Task.CompletedTask
                : FlushThroughputCheckpoints(live, elapsed, seconds, checkpointReports);
            var previousCheckpoint = live.LastCheckpoint;
            int minimumScheduled = int.MaxValue;
            double? recovered = null;
            long previousCompleted = completedAtStart;
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
                var gc = GC.GetGCMemoryInfo();
                if (nativeCheckpoints && live.LastCheckpoint is { } checkpoint && checkpoint != previousCheckpoint)
                {
                    checkpointReports.Enqueue(checkpoint);
                    previousCheckpoint = checkpoint;
                }
                long sampleAllocations = GC.GetTotalAllocatedBytes(precise: true) - startedAllocations;
                allocationSamplesMonotonic &= sampleAllocations >= timedAllocations;
                timedAllocations = sampleAllocations;
                process.Refresh();
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
                        GcCommittedBytes = gc.TotalCommittedBytes,
                        GcHardLimitBytes = gc.TotalAvailableMemoryBytes,
                        WorkingSetBytes = process.WorkingSet64,
                        PeakWorkingSetBytes = process.PeakWorkingSet64,
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
            double cpuMilliseconds = (process.TotalProcessorTime - startedCpu).TotalMilliseconds;
            long completedDuringReplay = previousCompleted - completedAtStart;
            long paidDuringReplay = scheduler.PaidCollections - paidAtStart;
            await arrivals;
            foreach (var session in sessions)
            {
                session.Dispose();
            }
            populationMaintenance.Dispose();
            maintenance.Dispose();
            activityRewards.Dispose();
            await checkpoints;
            if (marketMaintenance is not null)
            {
                await marketMaintenance.DisposeAsync();
                Assert.Null(marketMaintenance.LastFailure);
            }
            long stoppedAt = startingSequences is null ? 0 : host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
            await live.FlushAsync();
            if (live.LastCheckpoint is { } finalCheckpoint)
            {
                checkpointReports.Enqueue(finalCheckpoint);
            }
            var jobProgress = startingSequences is null ? null : ReadThroughputJobProgress(live, startingSequences, stoppedAt);

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
                        AdmissionMilliseconds = admission.Elapsed.TotalMilliseconds,
                        CompletedDuringReplay = completedDuringReplay,
                        PaidCollectionsDuringReplay = paidDuringReplay,
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
                        NativeCheckpoints = nativeCheckpoints,
                        CheckpointSeconds = database.Settings.Simulation.CheckpointSeconds,
                        MinimumScheduled = minimumScheduled,
                        JobProgress = jobProgress,
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
                        CpuMilliseconds = cpuMilliseconds,
                        AllocatedBytes = timedAllocations,
                        AllocationSamplesMonotonic = allocationSamplesMonotonic,
                        BytesPerCompletedAction = allocationSamplesMonotonic
                            ? timedAllocations / (double)Math.Max(1, completedDuringReplay)
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
                if (requiredPilots is { } required)
                {
                    Assert.True(minimumScheduled >= required, $"Minimum scheduled population was {minimumScheduled}.");
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

    private static async Task FlushThroughputCheckpoints(
        LiveWorld live,
        Stopwatch elapsed,
        int seconds,
        ConcurrentQueue<WorldCheckpoint> reports
    )
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
                reports.Enqueue(checkpoint);
            }
        }
    }

    private static ThroughputJobProgress ReadThroughputJobProgress(LiveWorld live, Dictionary<long, long> startingSequences, long stoppedAt)
    {
        int advanced = 0;
        int unchangedDue = 0;
        int unchangedFuture = 0;
        int missing = 0;
        using var owner = live.EnterPersistence("replay job progress end");
        foreach (var (pilot, sequence) in startingSequences)
        {
            if (!live.CapsuleerJobs.TryGetValue(pilot, out var job) && !live.ProjectedCapsuleerJobs.TryGetValue(pilot, out job))
            {
                missing++;
            }
            else if (job.Sequence > sequence)
            {
                advanced++;
            }
            else if (job.DueTicks <= stoppedAt)
            {
                unchangedDue++;
            }
            else
            {
                unchangedFuture++;
            }
        }
        return new(startingSequences.Count, advanced, unchangedDue, unchangedFuture, missing);
    }

    private static IEnumerable<int> SavedCapsuleerSystems(WorldDatabase database, WorldDbContext world, int[] communities)
    {
        var locations = (
            from agent in world.Set<SavedCapsuleerAgent>()
            where communities.Contains(agent.CommunitySystemId)
            join pilot in world.Pilots on agent.PilotId equals pilot.Id
            select new
            {
                pilot.ActiveShip.Position!.SystemId,
                pilot.ActiveShip.StructureId,
                pilot.ActiveShip.StationId,
            }
        )
            .Distinct()
            .ToArray();
        var structures = world.Set<SavedStructure>().ToDictionary(structure => structure.Id, structure => structure.SystemId);
        var catalog = database.Catalog;
        return locations.Select(location =>
            location.SystemId
            ?? (location.StructureId is long structure ? structures[structure] : catalog.FindStation(location.StationId)!.SolarSystemId)
        );
    }

    private static void CopyThroughputSidecars(string source, string destination, FrozenReplayCatalogs? immutableCatalogs)
    {
        foreach (string file in new[] { "traffic.sqlite", "world.sqlite.skills.sqlite", "world.sqlite.skills-active" })
        {
            if (File.Exists(Path.Combine(source, file)))
            {
                File.Copy(Path.Combine(source, file), Path.Combine(destination, file));
            }
        }
        foreach (string directory in new[] { "world.sqlite.skill-packages", "market-orders" })
        {
            string sidecars = Path.Combine(source, directory);
            if (!Directory.Exists(sidecars))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(sidecars, "*", SearchOption.AllDirectories))
            {
                string target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (
                    immutableCatalogs is not null
                    && directory == "world.sqlite.skill-packages"
                    && Path.GetFileName(file) == "catalog.sqlite"
                )
                {
                    immutableCatalogs.Link(file, target);
                }
                else
                {
                    File.Copy(file, target);
                }
            }
        }
    }

    private static async Task PrefillThroughputRoster(
        ICapsuleerBackgroundPreparation preparation,
        LiveWorld live,
        int target,
        int timeoutSeconds,
        string progress
    )
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int nextReport = live.Economy.AdmittedCapsuleerCount + 250;
        while (live.Economy.AdmittedCapsuleerCount < target)
        {
            var turn = preparation.PrepareNext();
            Assert.Null(turn.Failure);
            int founded = live.Economy.AdmittedCapsuleerCount;
            if (founded >= nextReport)
            {
                File.AppendAllText(progress, $"Prepared {founded} of {target} pilots for the timed workload.\n");
                nextReport = founded + 250;
            }
            Assert.True(
                founded >= target || !turn.Complete && elapsed.Elapsed.TotalSeconds < timeoutSeconds,
                $"Prepared only {founded} of {target} pilots before the timed workload."
            );
            if (turn.RetryLater && founded < target)
            {
                await Task.Delay(turn.RetryAfter ?? CapsuleerJobQueue.WakeInterval);
            }
        }
        File.AppendAllText(progress, $"Prepared roster in {elapsed.Elapsed.TotalSeconds:F1}s.\n");
    }

    private static async Task WaitForThroughputPopulation(
        ICapsuleerDiagnostics diagnostics,
        LiveWorld live,
        int target,
        int timeoutSeconds,
        string progress
    )
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        int nextCheckpoint = 15;
        while (true)
        {
            var state = diagnostics.Read();
            if (state.ScheduledAgents >= target)
            {
                File.AppendAllText(progress, $"Admitted {state.ScheduledAgents} pilots after {elapsed.Elapsed.TotalSeconds:F1}s.\n");
                return;
            }
            Assert.Equal(0, state.Failures);
            Assert.True(
                elapsed.Elapsed.TotalSeconds < timeoutSeconds,
                $"Admission timed out at {state.ScheduledAgents} scheduled, {state.Population.Agents} saved and {state.DueJobs} due."
            );
            if (elapsed.Elapsed.TotalSeconds >= nextCheckpoint)
            {
                await live.FlushAsync();
                nextCheckpoint += 15;
                File.AppendAllText(
                    progress,
                    $"admission={elapsed.Elapsed.TotalSeconds:F1}s scheduled={state.ScheduledAgents} "
                        + $"saved={state.Population.Agents} due={state.DueJobs}\n"
                );
            }
            await Task.Delay(TimeSpan.FromSeconds(1));
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

        internal ThroughputObservers(ServiceProvider host, TimeProvider clock, int count, DockedPilot primary, Action<string> report)
        {
            (long Pilot, long Account)[] identities = [(90000001, 1000001), (90000002, 1000002), (90000004, 1000004)];
            var ships = host.GetRequiredService<Eden.World.Docking.SqliteWorld>();
            foreach (var identity in identities.Where(identity => identity.Pilot != primary.PilotId).Take(count))
            {
                var pilot =
                    ships.FindPilot(identity.Account, identity.Pilot)
                    ?? throw new InvalidOperationException($"Saved observer {identity.Pilot} is unavailable.");
                ships.SetPosition(pilot.ShipId, new(primary.X + 2000000 + Players.Count * 100000, primary.Y, primary.Z), primary.SystemId);
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
