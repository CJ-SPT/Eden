using System.Collections.Concurrent;
using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayReportedSessionWithDueTrafficAndCheckpoint(bool concurrent)
    {
        string? source = Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE");
        if (source is null)
        {
            output.WriteLine("Set EDEN_PERFORMANCE_SOURCE to replay a frozen saved world.");
            return;
        }
        const long ReportedPilot = 90001835;
        const int ReportedStation = 60011866;
        const int WarmupSamples = 4;
        const int MeasuredSamples = 40;
        const int CheckpointSampleInterval = 15;
        string directory = CopySnapshot(source);
        try
        {
            WorldDatabase database = new(Path.Combine(directory, "world.sqlite"));
            TestClock clock = new(new DateTimeOffset(2026, 11, 9, 16, 0, 0, TimeSpan.Zero));
            WorldInitialization.Initialize(database, clock);
            long account;
            using (WorldDbContext world = new(database.Path))
            {
                var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == ReportedPilot);
                account = pilot.AccountId;
                if (pilot.ActiveShip.Position is { } position)
                {
                    world.Positions.Remove(position);
                }
                pilot.ActiveShip.StationId = ReportedStation;
                world.SaveChanges();
            }
            using var host = ServerServices.Build(database, clock, liveWorld: true);
            using var scope = host.CreateScope();
            var provider = scope.ServiceProvider;
            var lookup = provider.GetRequiredService<IPilotLookup>();
            using PilotCallContext context = new(
                account,
                lookup.ReadAccount(account),
                lookup.ListPilots(account),
                provider.GetRequiredService<FlightSessionFactory>().Create,
                account,
                SignedSettings.Empty
            );
            ConcurrentQueue<string> reports = new();
            List<object?> packets = [];
            PilotSessionOutput sink = new(
                context,
                account,
                account,
                packet =>
                {
                    packets.Add(packet);
                    return Task.CompletedTask;
                },
                reports.Enqueue
            );
            var updates = provider.GetRequiredService<PilotUpdateCoordinator>();
            var skills = provider.GetRequiredService<SkillHandler>();
            var operations = provider.GetRequiredService<IPilotOperationScope>();
            var handoff = provider.GetRequiredService<ICapsuleerHandoff>();
            var presentation = provider.GetRequiredService<ICapsuleerPresentation>();
            PilotSessionCalls calls = new(
                context,
                sink,
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
            PilotSessionTick tick = new(
                clock,
                updates,
                skills,
                provider.GetRequiredService<IAsteroidField>(),
                operations,
                handoff: handoff,
                presentation: presentation
            );
            long started = Stopwatch.GetTimestamp();
            await calls.ProcessAsync(Request("charUnboundMgr", "SelectCharacterID", ReportedPilot));
            reports.Enqueue($"Cold selection {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
            Assert.Equal(ReportedStation, context.Pilot.StationId);
            var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            List<double> callTimes = [];
            List<double> tickTimes = [];
            for (int sample = 0; sample < WarmupSamples + MeasuredSamples; sample++)
            {
                clock.Seconds++;
                Task background = Task.Run(async () =>
                {
                    using var metrics = SessionPerformance.Begin($"sample {sample} maintenance", reports.Enqueue, force: true);
                    maintenance.Tick();
                    if (sample % CheckpointSampleInterval == 0)
                    {
                        await live.FlushAsync();
                    }
                });
                if (!concurrent)
                {
                    await background;
                }
                int requestIndex = 0;
                foreach (
                    var request in new[]
                    {
                        Request("onlineStatus", "GetInitialState"),
                        Request("ship", "MachoResolveObject", new object?[] { (long)ReportedStation, 15L }),
                        Request("pingService", "Ping"),
                    }
                )
                {
                    using var metrics = SessionPerformance.Begin($"sample {sample} call", reports.Enqueue, force: true);
                    started = Stopwatch.GetTimestamp();
                    int previousPackets = packets.Count;
                    await calls.ProcessAsync(request);
                    Assert.True(packets.Count > previousPackets);
                    var reply = Assert.IsType<Eden.Protocol.Serialization.BlueInstance>(packets[previousPackets]);
                    Assert.EndsWith(requestIndex++ == 2 ? ".ErrorResponse" : ".CallRsp", reply.Name);
                    if (sample >= WarmupSamples)
                    {
                        callTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                }
                started = Stopwatch.GetTimestamp();
                using (SessionPerformance.Begin($"sample {sample} tick", reports.Enqueue, force: true))
                {
                    await tick.AdvanceAsync(context, sink, reports.Enqueue, account, account);
                }
                if (sample >= WarmupSamples)
                {
                    tickTimes.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                await background;
            }
            foreach (var group in new[] { (Name: "calls", Times: callTimes), (Name: "ticks", Times: tickTimes) })
            {
                double[] sorted = group.Times.Order().ToArray();
                reports.Enqueue(
                    $"Concurrent={concurrent} {group.Name}: p95={sorted[(int)Math.Ceiling(sorted.Length * .95) - 1]:F2} ms, "
                        + $"maximum={sorted[^1]:F2} ms, samples={sorted.Length}, over1s={sorted.Count(t => t >= 1000)}."
                );
            }
            reports.Enqueue($"Checkpoint {live.LastCheckpoint}.");
            Assert.True(context.Selected);
            Assert.Contains(packets.OfType<Eden.Protocol.Serialization.BlueInstance>(), p => p.Name.EndsWith(".CallRsp"));
            if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_OUTPUT") is { } destination)
            {
                Directory.CreateDirectory(destination);
                File.WriteAllLines(Path.Combine(destination, $"stalls-{concurrent}.log"), reports);
            }
            foreach (string line in reports.Where(l => l.StartsWith("Cold") || l.StartsWith("Concurrent") || l.StartsWith("Checkpoint")))
            {
                output.WriteLine(line);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
