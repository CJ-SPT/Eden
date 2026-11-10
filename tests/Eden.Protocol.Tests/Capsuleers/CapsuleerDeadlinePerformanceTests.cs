using System.Diagnostics;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Skills;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Docking;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerDeadlinePerformanceTests(ITestOutputHelper output)
{
    private sealed record Client(IServiceScope Scope, PilotCallContext Context, PilotSessionTick Tick, PilotSessionOutput Output);

    private sealed record Cargo(long Id, long OwnerId, int TypeId, long LocationId, long Quantity);

    private sealed record Pilot(long Id, SpacePosition? Position, MinerActivity? Mining, int Cycles, int Trips, HaulerActivity? Hauling);

    private sealed record Replay(
        int Turns,
        double Milliseconds,
        long AllocatedBytes,
        int Commands,
        int Connections,
        int Fits,
        long EncodedBytes,
        CapsuleerJob[] Jobs,
        Pilot[] Pilots,
        Cargo[] Cargo,
        double CaptureMilliseconds,
        double ProjectionMilliseconds,
        double WriteMilliseconds
    );

    private sealed record Comparison(bool Occupied, int Observers, Replay Polling, Replay Deadlines);

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task MatchedMaintenanceFramesPreserveJobsAndAssetsWithFewerIdleTurns(bool occupied, int observers)
    {
        var polling = await Measure(false, occupied, observers);
        var deadlines = await Measure(true, occupied, observers);
        Assert.Equal(polling.Jobs, deadlines.Jobs);
        Assert.Equal(polling.Pilots, deadlines.Pilots);
        Assert.Equal(polling.Cargo, deadlines.Cargo);
        Assert.True(deadlines.Turns < polling.Turns / 2);
        Assert.Equal(polling.Fits, deadlines.Fits);
        Assert.Equal(polling.EncodedBytes, deadlines.EncodedBytes);
        Assert.True(deadlines.Commands <= polling.Commands);
        Assert.True(deadlines.Connections <= polling.Connections);
        string report = JsonSerializer.Serialize(
            new Comparison(occupied, observers, polling, deadlines),
            new JsonSerializerOptions { IgnoreReadOnlyProperties = true }
        );
        output.WriteLine("DEADLINE_REPLAY=" + report);
        if (Environment.GetEnvironmentVariable("EDEN_DEADLINE_OUTPUT") is { } directory)
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, $"{occupied}-{observers}.json"), report);
        }
    }

    private static async Task<Replay> Measure(bool deadlines, bool occupied, int observers)
    {
        using CapsuleerHaulerTests.Fixture f = new(isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        var selections = Enumerable
            .Range(0, observers)
            .Select(index => presence.Select(90000001 + index, occupied ? 30000141 : 30000144))
            .ToArray();
        List<Client> clients = [];
        long encodedBytes = 0;
        try
        {
            for (int observer = 0; observer < observers; observer++)
            {
                var scope = host.CreateScope();
                var services = scope.ServiceProvider;
                var ships = services.GetRequiredService<SqliteWorld>();
                long account = 1000001 + observer;
                var pilot = ships.FindPilot(account)!;
                ships.SetPosition(pilot.ShipId, new(1_000_000 + observer * 1000, 0, 0), occupied ? 30000141 : 30000144);
                pilot = ships.FindPilot(account)!;
                PilotCallContext context = new(
                    account,
                    pilot.Account,
                    [pilot],
                    services.GetRequiredService<FlightSessionFactory>().Create,
                    account,
                    SignedSettings.Empty
                );
                context.Select(pilot);
                var updates = services.GetRequiredService<PilotUpdateCoordinator>();
                updates.Enter(context);
                context.Flight.Snapshot();
                clients.Add(
                    new(
                        scope,
                        context,
                        new(
                            f.Clock,
                            updates,
                            services.GetRequiredService<SkillHandler>(),
                            services.GetRequiredService<IAsteroidField>(),
                            services.GetRequiredService<IPilotOperationScope>()
                        ),
                        new(
                            context,
                            account,
                            account,
                            packet =>
                            {
                                encodedBytes += BlueMarshal.Encode(packet).Length;
                                return Task.CompletedTask;
                            },
                            _ => { }
                        )
                    )
                );
            }
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            f.Clock.Seconds = 1;
            do
            {
                worker.Tick();
            } while (scheduler.NextWake().Delay == TimeSpan.Zero);
            foreach (var client in clients)
            {
                await client.Tick.AdvanceAsync(client.Context, client.Output, _ => { }, client.Context.AccountId, client.Context.AccountId);
            }
            encodedBytes = 0;
            const int frames = 200;
            int turns = 0;
            var before = scheduler.NextWake();
            double milliseconds;
            long allocated;
            int commands;
            int connections;
            int fits;
            using (var metrics = SessionPerformance.Begin("matched maintenance frames", _ => { }, force: true)!)
            {
                long startingAllocation = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int frame = 0; frame < frames; frame++)
                {
                    f.Clock.Milliseconds += 50;
                    var next = scheduler.NextWake();
                    if (!deadlines || next.Delay == TimeSpan.Zero || before.Changed.IsCompleted)
                    {
                        do
                        {
                            worker.Tick();
                            turns++;
                        } while (scheduler.NextWake().Delay == TimeSpan.Zero);
                    }
                    before = scheduler.NextWake();
                    const int MaintenanceFramesPerSessionFrame = 20;
                    if ((frame + 1) % MaintenanceFramesPerSessionFrame == 0)
                    {
                        foreach (var client in clients)
                        {
                            await client.Tick.AdvanceAsync(
                                client.Context,
                                client.Output,
                                _ => { },
                                client.Context.AccountId,
                                client.Context.AccountId
                            );
                        }
                    }
                }
                milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                allocated = GC.GetAllocatedBytesForCurrentThread() - startingAllocation;
                commands = metrics.Commands;
                connections = metrics.Connections;
                fits = metrics.FittingCalculations;
            }
            var identities = host.GetRequiredService<ICapsuleerCommunity>().Pilots();
            var pilotStore = host.GetRequiredService<SqliteWorld>();
            var pilots = identities
                .Select(identity =>
                {
                    var miner = identity.Career == CapsuleerCareer.Mining ? scheduler.Miner(identity.PilotId) : null;
                    var hauler = identity.Career == CapsuleerCareer.Hauling ? scheduler.Hauler(identity.PilotId) : null;
                    return new Pilot(
                        identity.PilotId,
                        pilotStore.FindPilot(-1, identity.PilotId)!.Position,
                        miner?.Work.Activity,
                        miner?.Work.Cycles ?? 0,
                        miner?.Work.Trips ?? 0,
                        hauler?.Work.Activity
                    );
                })
                .ToArray();
            var jobs = scheduler.Jobs().ToArray();
            using WorldDbContext world = new(f.Database.Path);
            var cargo = world
                .Set<OwnedItem>()
                .AsNoTracking()
                .OrderBy(i => i.Id)
                .Select(i => new Cargo(i.Id, i.OwnerId, i.TypeId, i.LocationId, i.Quantity))
                .ToArray();
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            var checkpoint = Assert.IsType<WorldCheckpoint>(live.LastCheckpoint);
            return new(
                turns,
                milliseconds,
                allocated,
                commands,
                connections,
                fits,
                encodedBytes,
                jobs,
                pilots,
                cargo,
                checkpoint.Capture.TotalMilliseconds,
                checkpoint.Projection.TotalMilliseconds,
                checkpoint.Write.TotalMilliseconds
            );
        }
        finally
        {
            foreach (var client in clients)
            {
                client.Context.Dispose();
                client.Scope.Dispose();
            }
            foreach (var selection in selections)
            {
                selection.Dispose();
            }
        }
    }
}
