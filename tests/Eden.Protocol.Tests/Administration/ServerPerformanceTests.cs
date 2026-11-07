using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Administration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Administration;

public sealed class ServerPerformanceTests
{
    private static ServerResourceSample Sample(int cpuSeconds = 1, long allocated = 1024) =>
        new(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(cpuSeconds), 4, 4096, 2048, 1024, allocated, 3, 2, 1, 10, 4, 2);

    [Fact]
    public void SharedSamplesNormalizeCpuCacheReadersAndTrackAccountRelease()
    {
        TestClock clock = new();
        PilotGate pilots = new();
        int reads = 0;
        ServerResourceSample resources = Sample();
        ServerPerformanceMonitor monitor = new(
            clock,
            pilots,
            readResources: () =>
            {
                reads++;
                return resources;
            }
        );
        Assert.True(pilots.TryEnter(1));
        Assert.False(pilots.TryEnter(1));
        var first = monitor.Read();
        Assert.Null(first.CpuPercent);
        Assert.Null(first.AllocatedBytesPerSecond);
        Assert.Equal(1, first.ActiveSessions);
        Assert.False(first.ResidentWorld);
        Assert.Null(first.LastCheckpoint);
        Parallel.For(0, 20, _ => Assert.Same(first, monitor.Read()));
        Assert.Equal(1, reads);
        clock.Milliseconds = 1999;
        Assert.Same(first, monitor.Read());
        pilots.Leave(1);
        pilots.Leave(1);
        resources = Sample(cpuSeconds: 3, allocated: 5120);
        clock.Milliseconds = 2000;
        var second = monitor.Read();
        Assert.Equal(25, second.CpuPercent);
        Assert.Equal(2048, second.AllocatedBytesPerSecond);
        Assert.Equal(0, second.ActiveSessions);
        Assert.Equal(clock.GetUtcNow(), second.SampledAt);
        Assert.Equal(2, reads);
        Assert.Equal(1, first.ActiveSessions);
        // Wall-clock corrections cannot corrupt a rate calculated with monotonic elapsed time.
        clock.AdvanceWallTime(TimeSpan.FromHours(-1));
        clock.AdvanceElapsed(TimeSpan.FromSeconds(4));
        resources = Sample(cpuSeconds: 7, allocated: 9216);
        var delayed = monitor.Read();
        Assert.Equal(25, delayed.CpuPercent);
        Assert.Equal(1024, delayed.AllocatedBytesPerSecond);
        Assert.True(delayed.SampledAt < second.SampledAt);
    }

    [Fact]
    public void RatesStayWithinBoundsWhenApproximateCountersMoveBackwards()
    {
        TestClock clock = new();
        ServerResourceSample resources = Sample(cpuSeconds: 4, allocated: 5120);
        ServerPerformanceMonitor monitor = new(clock, new(), readResources: () => resources);
        _ = monitor.Read();
        clock.Seconds = 2;
        resources = Sample();
        var reset = monitor.Read();
        Assert.Equal(0, reset.CpuPercent);
        Assert.Equal(0, reset.AllocatedBytesPerSecond);
        clock.Seconds = 4;
        resources = Sample(cpuSeconds: 100);
        Assert.Equal(100, monitor.Read().CpuPercent);
    }

    [Fact]
    public async Task CheckpointDiagnosticsShowFailureAndRecoveryWithoutOpeningWorldConnections()
    {
        using TestWorld fixture = new();
        TestClock clock = new();
        bool fail = true;
        using LiveWorld world = new(
            new(fixture.Path),
            clock,
            _ => { },
            persist: (snapshot, path) =>
            {
                if (fail)
                {
                    throw new IOException("Test save failure");
                }
                LiveWorld.PersistSnapshot(snapshot, path);
            }
        );
        ServerPerformanceMonitor monitor = new(clock, new(), world, () => Sample());
        Assert.True(monitor.Read().ResidentWorld);
        Assert.Null(monitor.Read().LastCheckpoint);
        using (WorldDbContext database = new(fixture.Path))
        {
            database.Database.ExecuteSqlRaw("UPDATE WorldPilot SET Name='Performance checkpoint' WHERE Id=90000001");
        }
        await Assert.ThrowsAsync<IOException>(world.FlushAsync);
        clock.Seconds = 2;
        Assert.True(monitor.Read().CheckpointFailed);
        Assert.Null(monitor.Read().LastCheckpoint);
        fail = false;
        await world.FlushAsync();
        clock.Seconds = 4;
        using var metrics = SessionPerformance.Begin("performance-panel", _ => { }, force: true)!;
        var recovered = monitor.Read();
        var checkpoint = Assert.IsType<ServerCheckpointPerformance>(recovered.LastCheckpoint);
        Assert.False(recovered.CheckpointFailed);
        Assert.Equal(world.LastCheckpoint!.Generation, checkpoint.Generation);
        Assert.Equal(world.LastCheckpoint.CompletedAt, checkpoint.CompletedAt);
        Assert.Equal(world.LastCheckpoint.Wait, checkpoint.Wait);
        Assert.Equal(world.LastCheckpoint.Capture, checkpoint.Capture);
        Assert.Equal(world.LastCheckpoint.Projection, checkpoint.Projection);
        Assert.Equal(world.LastCheckpoint.Write, checkpoint.Write);
        Assert.True(checkpoint.Bytes > 0);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.FittingCalculations);
    }
}
