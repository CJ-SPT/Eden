using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    private sealed record ReplaySample(string Operation, double Reply, double Frame, int Sql, int Connections, int Fits);

    [Fact]
    public async Task ReplayTwoPlayersUnderRemoteTrafficAndPopulationLoad()
    {
        if (Environment.GetEnvironmentVariable("EDEN_SYSTEM_REPLAY_SOURCE") is not { } source)
        {
            output.WriteLine("Set EDEN_SYSTEM_REPLAY_SOURCE to a frozen directory produced by tools/Freeze-SystemReplay.py.");
            return;
        }
        const int BusySystem = 30002659;
        long FlyingPilot = Environment.GetEnvironmentVariable("EDEN_REPORTED_TRAFFIC") == "1" ? 90000006 : 90000001;
        long flyingAccount = FlyingPilot == 90000006 ? 1000003 : 1000001;
        const long DockedPilot = 90000002;
        const int Warmup = 4;
        int samples = int.Parse(Environment.GetEnvironmentVariable("EDEN_SYSTEM_REPLAY_SAMPLES") ?? "40");
        string directory = CopySnapshot(source);
        ConcurrentQueue<string> reports = new();
        ConcurrentQueue<OwnershipTiming> ownership = new();
        try
        {
            foreach (string file in new[] { "traffic.sqlite", "world.sqlite.skills.sqlite", "world.sqlite.skills-active" })
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
            TestClock clock = new(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock);
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            var movement = host.GetRequiredService<Eden.World.Docking.SqliteWorld>();
            var flying = movement.FindPilot(flyingAccount, FlyingPilot)!;
            movement.SetPosition(flying.ShipId, new(flying.X + 2000000, flying.Y, flying.Z), flying.SystemId);
            using var pilot = new ReplayPlayer(host, clock, FlyingPilot, flyingAccount, reports);
            using var docked = new ReplayPlayer(host, clock, DockedPilot, 1000002, reports);
            if (FlyingPilot == 90000006)
            {
                Assert.Equal(30002187, pilot.Context.Pilot.SystemId);
            }
            else
            {
                Assert.Equal(pilot.Context.Pilot.SystemId, docked.Context.Pilot.SystemId);
            }
            Assert.NotEqual(BusySystem, pilot.Context.Pilot.SystemId);
            using var presence = host.GetRequiredService<IHumanPresence>().Select(90000003, BusySystem);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var population = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
            var diagnostics = host.GetRequiredService<ICapsuleerDiagnostics>();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            pilot.Context.Flight.Snapshot();
            clock.Seconds += 10;
            await pilot.Frame();
            await docked.Frame();
            List<ReplaySample> measured = [];
            using var observation = OwnershipMonitor.Observe(ownership.Enqueue);
            using var checkpointObservation = live.ObserveOwnership(ownership.Enqueue);
            for (int sample = 0; sample < Warmup + samples; sample++)
            {
                clock.Seconds++;
                long allocated = GC.GetTotalAllocatedBytes(precise: true);
                using ManualResetEventSlim start = new();
                var background = Task.Run(async () =>
                {
                    Assert.True(start.Wait(TimeSpan.FromSeconds(30)));
                    using var metrics = SessionPerformance.Begin($"background {sample}", reports.Enqueue, force: true);
                    var prepared = population.PrepareNext();
                    Assert.Null(prepared.Failure);
                    maintenance.Tick();
                    if (sample % 15 == 0)
                    {
                        await live.FlushAsync();
                    }
                });
                var spaceWork = Task.Run(async () =>
                {
                    Assert.True(start.Wait(TimeSpan.FromSeconds(30)));
                    return new[]
                    {
                        await pilot.Call("beyonce", "CmdGotoDirection", 1d, sample % 2 == 0 ? 0d : 1d, 0d),
                        await pilot.Frame(),
                    };
                });
                var stationWork = Task.Run(async () =>
                {
                    Assert.True(start.Wait(TimeSpan.FromSeconds(30)));
                    return await docked.Call("station", "GetGuests");
                });
                start.Set();
                await Task.WhenAll(background, spaceWork, stationWork).WaitAsync(TimeSpan.FromMinutes(2));
                if (sample >= Warmup)
                {
                    measured.AddRange(await spaceWork);
                    measured.Add(await stationWork);
                }
                var traffic = diagnostics.Read();
                reports.Enqueue(
                    $"Progress sample={sample} allocatedBytes={GC.GetTotalAllocatedBytes(precise: true) - allocated} "
                        + $"completed={traffic.CompletedActions} due={traffic.DueJobs} "
                        + $"oldestMs={TimeSpan.FromTicks(traffic.OldestDelayTicks).TotalMilliseconds:F2} failures={traffic.Failures}."
                );
            }
            await live.FlushAsync();
            Assert.True(diagnostics.Read().CompletedActions > 0);
            Assert.Equal(0, diagnostics.Read().Failures);
            Assert.Contains(reports, line => line.StartsWith("Performance traffic.tradeAndLaunch:", StringComparison.Ordinal));
            Assert.Contains(reports, line => line.StartsWith("Performance traffic.completeJump:", StringComparison.Ordinal));
            foreach (var group in measured.GroupBy(s => s.Operation))
            {
                var frames = group.Select(s => s.Frame).Order().ToArray();
                var replies = group.Select(s => s.Reply).Where(s => s > 0).Order().ToArray();
                reports.Enqueue(
                    $"Result {group.Key} frame p95={Percentile(frames, .95):F2} p99={Percentile(frames, .99):F2} "
                        + $"max={frames[^1]:F2} over1s={frames.Count(v => v >= 1000)} "
                        + $"replyP95={Percentile(replies, .95):F2} replyP99={Percentile(replies, .99):F2} "
                        + $"replyMax={(replies.Length == 0 ? 0 : replies[^1]):F2} "
                        + $"sql={group.Sum(s => s.Sql)} connections={group.Sum(s => s.Connections)} fits={group.Sum(s => s.Fits)}."
                );
            }
            reports.Enqueue($"Checkpoint {live.LastCheckpoint}.");
            reports.Enqueue($"Settings {JsonSerializer.Serialize(database.Settings)}");
            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, "systems.log"), reports);
                File.WriteAllText(Path.Combine(destination, "ownership.json"), JsonSerializer.Serialize(ownership));
                File.WriteAllText(Path.Combine(destination, "samples.json"), JsonSerializer.Serialize(measured));
                File.Copy(Path.Combine(source, "manifest.json"), Path.Combine(destination, "manifest.json"), overwrite: true);
            }
            foreach (string line in reports.Where(l => l.StartsWith("Result") || l.StartsWith("Progress") || l.StartsWith("Checkpoint")))
            {
                output.WriteLine(line);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static double Percentile(double[] values, double percentile) =>
        values.Length == 0 ? 0 : values[(int)Math.Ceiling(values.Length * percentile) - 1];

    private sealed class ReplayPlayer : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly ConcurrentQueue<string> _reports;
        private readonly PilotSessionOutput _output;
        private readonly PilotSessionCalls _calls;
        private readonly PilotSessionTick _tick;
        private readonly long _account;
        private long _arrival;
        private double _reply;
        internal PilotCallContext Context { get; }

        internal ReplayPlayer(ServiceProvider host, TestClock clock, long pilot, long account, ConcurrentQueue<string> reports)
        {
            _scope = host.CreateScope();
            _account = account;
            _reports = reports;
            var provider = _scope.ServiceProvider;
            var lookup = provider.GetRequiredService<IPilotLookup>();
            Context = new(
                account,
                lookup.ReadAccount(account),
                lookup.ListPilots(account),
                provider.GetRequiredService<FlightSessionFactory>().Create,
                account,
                SignedSettings.Empty
            );
            Context.Select(provider.GetRequiredService<Eden.World.Docking.SqliteWorld>().FindPilot(account, pilot)!);
            _output = new(
                Context,
                account,
                account,
                packet =>
                {
                    Assert.False(provider.GetRequiredService<IWorldSynchronization>().IsEntered);
                    Assert.NotEmpty(BlueMarshal.Encode(packet));
                    if (packet is BlueInstance instance && instance.Name.EndsWith(".CallRsp", StringComparison.Ordinal))
                    {
                        _reply = Stopwatch.GetElapsedTime(_arrival).TotalMilliseconds;
                    }
                    Assert.False(packet is BlueInstance error && error.Name.EndsWith(".ErrorResponse", StringComparison.Ordinal));
                    return Task.CompletedTask;
                },
                reports.Enqueue
            );
            var updates = provider.GetRequiredService<PilotUpdateCoordinator>();
            var skills = provider.GetRequiredService<SkillHandler>();
            var operations = provider.GetRequiredService<IPilotOperationScope>();
            var handoff = provider.GetRequiredService<ICapsuleerHandoff>();
            var presentation = provider.GetRequiredService<CapsuleerMaintenance>();
            _calls = new(
                Context,
                _output,
                provider.GetRequiredService<PilotCallRouter>(),
                updates,
                skills,
                clock,
                reports.Enqueue,
                account,
                account,
                operations,
                handoff: handoff,
                presentation: presentation
            );
            _tick = new(
                clock,
                updates,
                skills,
                provider.GetRequiredService<IAsteroidField>(),
                operations,
                anomalies: provider.GetRequiredService<Eden.Protocol.Exploration.CombatAnomalyHandler>(),
                handoff: handoff,
                presentation: presentation,
                structures: provider.GetRequiredService<Eden.Gameplay.Structures.IStructureContacts>()
            );
            updates.Enter(Context);
        }

        internal async Task<ReplaySample> Call(string service, string method, params object?[] arguments)
        {
            _arrival = Stopwatch.GetTimestamp();
            _reply = 0;
            using var metrics = SessionPerformance.Begin($"player {service}.{method}", _reports.Enqueue, force: true)!;
            await _calls.ProcessAsync(Request(service, method, arguments), _arrival);
            Assert.True(_reply > 0);
            return new(
                method,
                _reply,
                Stopwatch.GetElapsedTime(_arrival).TotalMilliseconds,
                metrics.Commands,
                metrics.Connections,
                metrics.FittingCalculations
            );
        }

        internal async Task<ReplaySample> Frame()
        {
            long started = Stopwatch.GetTimestamp();
            using var metrics = SessionPerformance.Begin("player frame", _reports.Enqueue, force: true)!;
            await _tick.AdvanceAsync(Context, _output, _reports.Enqueue, _account, _account);
            return new(
                "frame",
                0,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                metrics.Commands,
                metrics.Connections,
                metrics.FittingCalculations
            );
        }

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
        }
    }
}
