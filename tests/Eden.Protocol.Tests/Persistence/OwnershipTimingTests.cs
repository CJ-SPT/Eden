using System.Collections.Concurrent;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Flight;
using Eden.Server;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class OwnershipTimingTests
{
    [Fact]
    public async Task SessionHandoffSelectsTheRuntimeWithoutOwningTheCompleteFrame()
    {
        using TestWorld fixture = new();
        using var host = ServerServices.Build(new(fixture.Path), new TestClock(), liveWorld: true);
        List<OwnershipTiming> timings = [];
        using var observation = OwnershipMonitor.Observe(timings.Add);
        const int RequestedSystem = 30000144;
        await host.GetRequiredService<ICapsuleerHandoff>()
            .SynchronizeAsync(
                RequestedSystem,
                () =>
                {
                    Assert.Equal(RequestedSystem, host.GetRequiredService<LiveWorld>().CurrentSystem);
                    Assert.False(host.GetRequiredService<LiveWorld>().IsEntered);
                }
            );
        Assert.Empty(timings);
    }

    [Fact]
    public async Task FailedTimingSinkCannotTurnCommittedWriteIntoFailedConnectionClose()
    {
        using TestWorld fixture = new();
        using LiveWorld live = new(new(fixture.Path), new TestClock(), _ => { });
        using var subscription = live.ObserveOwnership(_ => throw new IOException("Closed diagnostic sink"));
        long expected;
        using (WorldDbContext world = new(fixture.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            expected =
                world
                    .Set<Eden.Data.World.Market.PilotWallet>()
                    .Where(wallet => wallet.PilotId == 90000001)
                    .Select(wallet => wallet.Cents)
                    .SingleOrDefault() + 1;
            world.Database.ExecuteSqlRaw(
                "INSERT INTO WorldWallet(PilotId,Cents) VALUES(90000001,1) " + "ON CONFLICT(PilotId) DO UPDATE SET Cents=Cents+1"
            );
            transaction.Commit();
        }
        await live.FlushAsync();
        Assert.NotNull(live.LastCheckpoint);
        Assert.Null(live.LastSaveFailure);
        Assert.False(live.IsEntered);
        using var disk = WorldConnections.FileConnection(fixture.Path);
        disk.Open();
        using var query = disk.CreateCommand();
        query.CommandText = "SELECT Cents FROM WorldWallet WHERE PilotId=90000001";
        Assert.Equal(expected, (long)query.ExecuteScalar()!);
    }

    [Fact]
    public async Task CheckpointCaptureIsMeasuredAfterExecutionContextIsDetached()
    {
        using TestWorld fixture = new();
        using LiveWorld world = new(new(fixture.Path), new TestClock(), _ => { });
        ConcurrentQueue<OwnershipTiming> timings = new();
        using var subscription = world.ObserveOwnership(timings.Enqueue);
        using (world.Enter("dirty resident"))
        {
            world.ObjectsChanged();
        }
        await world.FlushAsync();
        var capture = Assert.Single(timings, timing => timing.Operation == "checkpoint capture");
        var checkpoint = Assert.IsType<WorldCheckpoint>(world.LastCheckpoint);
        Assert.True(checkpoint.Copy > TimeSpan.Zero);
        Assert.True(capture.Hold >= checkpoint.Copy);
        Assert.True(checkpoint.Capture >= checkpoint.Copy);
    }

    [Fact]
    public void NestedFlightAcquisitionsAreMeasuredOnceAndReleaseOnFailure()
    {
        using TestWorld fixture = new();
        using var host = ServerServices.Build(new(fixture.Path), new TestClock(), liveWorld: true);
        var space = host.GetRequiredService<SharedSpace>();
        List<OwnershipTiming> timings = [];
        using var observation = OwnershipMonitor.Observe(timings.Add);
        Assert.Throws<InvalidOperationException>(
            (Action)(
                () =>
                {
                    using var first = space.EnterMotion("test flight");
                    using var nested = host.GetRequiredService<LiveWorld>().Enter("nested SQL");
                    throw new InvalidOperationException("failed operation");
                }
            )
        );
        var timing = Assert.Single(timings);
        Assert.Equal("world", timing.Boundary);
        Assert.Equal("flight.test flight", timing.Operation);
        Assert.True(timing.Hold > TimeSpan.Zero);
        using (space.EnterMotion("next command")) { }
        Assert.Equal(2, timings.Count);
    }

    [Fact]
    public async Task ContendedFlightWaitIdentifiesItsHolderAndIncludesArrivalQueue()
    {
        using TestWorld fixture = new();
        using var host = ServerServices.Build(new(fixture.Path), new TestClock(), liveWorld: true);
        var space = host.GetRequiredService<SharedSpace>();
        using ManualResetEventSlim acquired = new();
        using ManualResetEventSlim release = new();
        ConcurrentQueue<OwnershipTiming> timings = new();
        using var observation = OwnershipMonitor.Observe(timings.Enqueue);
        var holder = Task.Run(() =>
        {
            using var lease = space.EnterMotion("busy A");
            acquired.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        try
        {
            Assert.True(acquired.Wait(TimeSpan.FromSeconds(10)));
            var waiter = Task.Run(() =>
            {
                using var latency = new SessionLatency(
                    "quiet B",
                    _ => { },
                    received: System.Diagnostics.Stopwatch.GetTimestamp() - System.Diagnostics.Stopwatch.Frequency
                );
                using (space.EnterMotion("quiet B")) { }
                latency.ReplyWritten();
                Assert.True(latency.ArrivalToReply >= TimeSpan.FromSeconds(1));
            });
            Assert.True(SpinWait.SpinUntil(() => host.GetRequiredService<LiveWorld>().WaitingOwners == 1, TimeSpan.FromSeconds(10)));
            release.Set();
            await Task.WhenAll(holder, waiter).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, timings.Count);
            Assert.All(timings, timing => Assert.True(timing.Hold > TimeSpan.Zero));
            Assert.Equal("flight.busy A", Assert.Single(timings, timing => timing.Operation == "flight.quiet B").Blocker);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public async Task WaitingFlightCommandRunsBeforeLaterTrafficReacquisitions()
    {
        OwnershipMonitor owner = new("test world");
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        ConcurrentQueue<string> order = new();
        var first = Task.Run(() =>
        {
            using (owner.Enter("first traffic job", 1))
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                using var nested = owner.Enter("nested transaction", 1);
            }
            using (owner.Enter("next traffic job", 1))
            {
                order.Enqueue("next traffic");
            }
        });
        Task? player = null;
        Task? later = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            player = Task.Run(() =>
            {
                using var flight = owner.Enter("flight", 2);
                order.Enqueue("flight");
            });
            Assert.True(SpinWait.SpinUntil(() => owner.WaitingCount == 1, TimeSpan.FromSeconds(10)));
            later = Task.Run(() =>
            {
                using var traffic = owner.Enter("later traffic", 1);
                order.Enqueue("later traffic");
            });
            Assert.True(SpinWait.SpinUntil(() => owner.WaitingCount == 2, TimeSpan.FromSeconds(10)));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(new[] { first, player, later }.OfType<Task>()).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(new[] { "flight", "later traffic", "next traffic" }, order);
        Assert.Equal(0, owner.WaitingCount);
    }
}
