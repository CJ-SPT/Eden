using System.Collections.Concurrent;
using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Fact]
    public void ReplaySavedLongDistanceHaulerReturnRoute()
    {
        if (Environment.GetEnvironmentVariable("EDEN_PERFORMANCE_SOURCE") is not { } source)
        {
            return;
        }
        // The reported save contains a 17-jump journey between these stations.
        using var scope = new CatalogReadScope().Begin();
        using CatalogDbContext catalog = new(Path.Combine(source, "catalog.sqlite"));
        const int sourceStation = 60000505;
        const int homeStation = 60009682;
        using (SessionPerformance.Begin("route catalog preparation", output.WriteLine, force: true))
        {
            CapsuleerTrafficRoutes.Prepare(catalog);
        }
        using var metrics = SessionPerformance.Begin("return route", output.WriteLine, force: true)!;
        var route = CapsuleerTrafficRoutes.ToStation(catalog, sourceStation, homeStation, HullClass.Hauler);
        Assert.NotNull(route);
        Assert.Equal(
            [
                30000148,
                30000146,
                30000131,
                30000126,
                30000128,
                30000127,
                30002788,
                30002790,
                30002786,
                30002768,
                30002765,
                30002764,
                30002761,
                30004972,
                30004970,
                30002633,
                30002634,
                30002655,
            ],
            route.Systems
        );
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

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
        int measuredSamples = int.TryParse(Environment.GetEnvironmentVariable("EDEN_TRAFFIC_REPLAY_SECONDS"), out int seconds)
            ? Math.Max(1, seconds)
            : 40;
        const int CheckpointSampleInterval = 15;
        string directory = CopySnapshot(source);
        try
        {
            string traffic = Path.Combine(source, "traffic.sqlite");
            if (File.Exists(traffic))
            {
                File.Copy(traffic, Path.Combine(directory, "traffic.sqlite"));
            }
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
            var ownership = new ReplayTrafficOwnership(provider.GetRequiredService<IWorldSynchronization>());
            using var maintenance = new CapsuleerMaintenance(
                provider.GetRequiredService<ICapsuleerScheduler>(),
                handoff,
                provider.GetRequiredService<ICapsuleerScene>(),
                provider.GetRequiredService<ChatRuntime>(),
                clock,
                ownership,
                provider.GetRequiredService<ICapsuleerUpdateDiagnostics>(),
                operations
            );
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
                presentation: maintenance
            );
            PilotSessionTick tick = new(
                clock,
                updates,
                skills,
                provider.GetRequiredService<IAsteroidField>(),
                operations,
                handoff: handoff,
                presentation: maintenance
            );
            Task coldTraffic = Task.Run(() => maintenance.Tick());
            await ownership.FirstAcquisition.Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (!concurrent)
            {
                await coldTraffic;
            }
            long started = Stopwatch.GetTimestamp();
            try
            {
                await calls.ProcessAsync(Request("charUnboundMgr", "SelectCharacterID", ReportedPilot));
            }
            finally
            {
                reports.Enqueue($"Cold selection {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
                await coldTraffic;
            }
            Assert.Equal(ReportedStation, context.Pilot.StationId);
            reports.Enqueue($"Cold traffic maximum hold {ownership.Holds.Max():F2} ms.");
            ownership.Holds.Clear();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            List<double> callTimes = [];
            List<double> tickTimes = [];
            List<double> longestHoldPerWake = [];
            for (int sample = 0; sample < WarmupSamples + measuredSamples; sample++)
            {
                clock.Seconds++;
                Task background = Task.Run(async () =>
                {
                    using var metrics = SessionPerformance.Begin($"sample {sample} maintenance", reports.Enqueue, force: true);
                    int firstHold = ownership.Holds.Count;
                    maintenance.Tick();
                    longestHoldPerWake.Add(ownership.Holds.Skip(firstHold).Max());
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
            var holds = longestHoldPerWake.Order().ToArray();
            reports.Enqueue(
                $"Concurrent={concurrent} longest traffic hold per wake: p95={holds[(int)Math.Ceiling(holds.Length * .95) - 1]:F2} ms, "
                    + $"maximum={holds[^1]:F2} ms, wakes={holds.Length}."
            );
            Assert.True(context.Selected);
            Assert.Contains(packets.OfType<Eden.Protocol.Serialization.BlueInstance>(), p => p.Name.EndsWith(".CallRsp"));
            // The native panel obtains the count before priming the names it needs to display each row.
            var router = provider.GetRequiredService<PilotCallRouter>();
            var roster = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "station", "GetGuests", Array.Empty<object?>(), reports.Enqueue)
            );
            var guests = Assert.IsType<BlueList<StationGuest>>(roster.Reply).Items;
            Assert.Contains(guests, guest => guest.CharacterId == ReportedPilot);
            var ids = guests.Select(guest => guest.CharacterId).Order().ToArray();
            var names = Assert.IsType<PilotCallResult.Success>(
                router.Call(context, "config", "GetMultiOwnersEx", new object?[] { ids.Cast<object?>().ToList() }, reports.Enqueue)
            );
            var owners = Assert.IsType<OwnerTable>(names.Reply).Owners;
            Assert.Equal(ids, owners.Select(owner => owner.Id).Order());
            Assert.All(owners, owner => Assert.False(string.IsNullOrWhiteSpace(owner.Name)));
            Assert.NotEmpty(BlueMarshal.Encode(names.Reply));
            output.WriteLine($"Resolved names for all {ids.Length} station guests.");
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

    private sealed class ReplayTrafficOwnership(IWorldSynchronization actual) : IWorldSynchronization
    {
        public object Gate => actual.Gate;
        public TaskCompletionSource FirstAcquisition { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<double> Holds { get; } = [];

        public IDisposable Enter(string operation)
        {
            bool nested = Monitor.IsEntered(Gate);
            var lease = actual.Enter(operation);
            if (nested || SessionLatency.CurrentDescription is not null)
            {
                return lease;
            }
            var measured = new MeasuredLease(lease, Holds, Stopwatch.GetTimestamp());
            FirstAcquisition.TrySetResult();
            return measured;
        }

        private sealed class MeasuredLease(IDisposable lease, List<double> holds, long started) : IDisposable
        {
            public void Dispose()
            {
                holds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                lease.Dispose();
            }
        }
    }
}
