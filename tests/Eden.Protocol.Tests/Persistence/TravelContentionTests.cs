using System.Diagnostics;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Persistence;

public sealed class TravelContentionTests(ITestOutputHelper output)
{
    private const long Account = 1000001;
    private const int UnrelatedSystem = 31000001;

    [Fact]
    public async Task GateHandoffDoesNotAcquireAnUnrelatedSystemAndCheckpointRetainsArrival()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        using ResidentFitCache fits = new(new(fixture.Path));
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        live.Economy.Warm();
        var pilot = fixture.Store.FindPilot(Account)!;
        var gate = fixture.Objects.Single(body => body.DestinationGateId.HasValue);
        var travel = fixture.Services.GetRequiredService<IPilotGateTravel>();
        fixture.Store.SetPosition(pilot.ShipId, gate.Position, pilot.SystemId);
        var first = travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.DestinationGateId!.Value);
        var exit = fixture
            .Services.GetRequiredService<ISystemObjects>()
            .Load(first.SystemId)
            .Single(body => body.Id == gate.DestinationGateId);
        fixture.Store.SetPosition(pilot.ShipId, exit.Position, first.SystemId);
        travel.Transfer(pilot.PilotId, pilot.ShipId, exit.Id, gate.Id);
        fixture.Store.SetPosition(pilot.ShipId, gate.Position, pilot.SystemId);

        var arrived = await WhileUnrelatedSystemIsPaused(
            live,
            () =>
            {
                using var context = live.UseSystem(pilot.SystemId);
                using var metrics = SessionPerformance.Begin("isolated gate handoff", output.WriteLine, force: true)!;
                long started = Stopwatch.GetTimestamp();
                Assert.Throws<InvalidDataException>(() => live.Run(() => travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, gate.Id)));
                Assert.Equal(gate.Position, live.Run(() => fixture.Store.FindPilot(Account, pilot.PilotId))!.Position);
                var result = live.Run(() => travel.Transfer(pilot.PilotId, pilot.ShipId, gate.Id, exit.Id));
                Assert.Equal(first.SystemId, result.SystemId);
                using (live.UseSystem(result.SystemId))
                {
                    Assert.Equal(result, live.Run(() => fixture.Store.FindPilot(Account, pilot.PilotId)));
                }
                output.WriteLine($"Gate handoff and rejection/readback: {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms.");
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
                return result;
            }
        );

        long checkpointStarted = Stopwatch.GetTimestamp();
        await live.FlushAsync();
        output.WriteLine($"Checkpoint: {Stopwatch.GetElapsedTime(checkpointStarted).TotalMilliseconds:F2} ms.");
        using var disk = WorldConnections.FileConnection(fixture.Path);
        using WorldDbContext reopened = new(disk);
        var position = reopened.Positions.Find(pilot.ShipId)!;
        Assert.Equal(arrived.SystemId, position.SystemId);
        Assert.Equal(arrived.Position, new SpacePosition(position.X, position.Y, position.Z));
    }

    private static async Task<T> WhileUnrelatedSystemIsPaused<T>(LiveWorld live, Func<T> action)
    {
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        var unrelated = Task.Run(() =>
        {
            using var ownership = live.Enter("paused unrelated travel observer", UnrelatedSystem);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        });
        Task<T>? operation = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            operation = Task.Run(action);
            return await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await unrelated;
            if (operation is not null)
            {
                await operation;
            }
        }
    }
}
