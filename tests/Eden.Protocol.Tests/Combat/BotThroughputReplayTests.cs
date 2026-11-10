using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private sealed class ThroughputClock : TimeProvider
    {
        internal TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + Offset;

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
        int seconds = int.Parse(Environment.GetEnvironmentVariable("EDEN_BOT_REPLAY_SECONDS") ?? "600");
        string destination =
            Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT")
            ?? throw new InvalidOperationException("Set EDEN_PERFORMANCE_OUTPUT to retain throughput results.");
        Directory.CreateDirectory(destination);
        string name = $"throughput-{systems}-{backlog}";
        string progress = Path.Combine(destination, name + ".progress");
        File.WriteAllText(progress, "Copying frozen source.\n");
        string directory = CopySnapshot(source);
        ConcurrentQueue<string> reports = new();
        List<object> samples = [];
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
                communities = world
                    .Database.SqlQuery<int>(
                        $"""
                        SELECT CommunitySystemId AS Value FROM WorldCapsuleerAgent
                        GROUP BY CommunitySystemId ORDER BY ABS(COUNT(*)-14),CommunitySystemId LIMIT {systems}
                        """
                    )
                    .ToArray();
            }
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            using var player = new ReplayPlayer(host, clock, 90000006, 1000003, reports);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var diagnostics = host.GetRequiredService<ICapsuleerDiagnostics>();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            using (var explorer = host.GetRequiredService<IHumanPresence>().Select(90000001, communities[0]))
            {
                foreach (int system in communities)
                {
                    explorer.Transfer(system);
                    File.AppendAllText(progress, $"Activated {system}.\n");
                }
            }
            await player.Frame();
            if (backlog)
            {
                clock.Offset = TimeSpan.FromSeconds(70);
            }
            using var metrics = Eden.Infrastructure.Diagnostics.SessionPerformance.Begin(
                "sustained bot worker",
                reports.Enqueue,
                force: true
            );
            maintenance.Start();
            Stopwatch elapsed = Stopwatch.StartNew();
            double nextCheckpoint = 15;
            double? recovered = null;
            long previousCompleted = 0;
            int previousDue = diagnostics.Read().DueJobs;
            double previousSeconds = 0;
            List<double> finalDelays = [];
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                await Task.Delay(TimeSpan.FromSeconds(1));
                var frame = await player.Frame();
                if (elapsed.Elapsed.TotalSeconds >= nextCheckpoint)
                {
                    await live.FlushAsync();
                    nextCheckpoint += 15;
                }
                var state = diagnostics.Read();
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
                        Frame = frame,
                    }
                );
                previousCompleted = state.CompletedActions;
                previousDue = state.DueJobs;
                previousSeconds = elapsed.Elapsed.TotalSeconds;
                File.AppendAllText(
                    progress,
                    $"seconds={elapsed.Elapsed.TotalSeconds:F1} scheduled={state.ScheduledAgents} "
                        + $"due={state.DueJobs} delayMs={delay:F1} completed={state.CompletedActions}\n"
                );
                Assert.Equal(0, state.Failures);
            }
            maintenance.Dispose();
            await live.FlushAsync();

            File.WriteAllText(
                Path.Combine(destination, name + ".json"),
                JsonSerializer.Serialize(
                    new
                    {
                        systems,
                        backlog,
                        seconds,
                        RecoveredSeconds = recovered,
                        Checkpoints = live.RecentCheckpoints,
                        Samples = samples,
                    }
                )
            );
            File.WriteAllLines(Path.Combine(destination, name + ".log"), reports);
            File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(destination, name + ".manifest.json"), overwrite: true);
            output.WriteLine($"Systems={systems} backlog={backlog} recovered={recovered} scheduled={diagnostics.Read().ScheduledAgents}");
            if (systems == 450 && seconds >= 600)
            {
                Assert.NotNull(recovered);
                Assert.True(recovered <= 120);
                Assert.All(finalDelays, delay => Assert.True(delay < 1000));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
