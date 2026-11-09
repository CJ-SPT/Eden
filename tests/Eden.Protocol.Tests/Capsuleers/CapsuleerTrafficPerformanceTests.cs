using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 2, false)]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, true)]
    [InlineData(true, 2, true)]
    public async Task WarmedMaintenanceIncludesSharedPresentationWithoutSqlOrFittingWork(bool occupied, int observers, bool imported)
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        if (imported)
        {
            TrafficSnapshotTests.Install(f.Database, TrafficSnapshotTests.Profile());
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var presence = host.GetRequiredService<IHumanPresence>();
        var leases = Enumerable.Range(0, observers).Select(i => presence.Select(90000001 + i, occupied ? 30000141 : 30000144)).ToArray();
        try
        {
            var worker = host.GetRequiredService<CapsuleerMaintenance>();
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            worker.Tick();
            var before = scheduler.Jobs().ToArray();
            const int wakes = 200;
            using (var metrics = SessionPerformance.Begin("warmed complete traffic wakes", output.WriteLine, force: true)!)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                for (int i = 0; i < wakes; i++)
                {
                    worker.Tick();
                }
                var elapsed = Stopwatch.GetElapsedTime(started);
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
                output.WriteLine(
                    $"Occupied={occupied}, observers={observers}, {wakes} wakes: {elapsed.TotalMilliseconds:F2} ms, {allocated} bytes."
                );
            }
            Assert.Equal(before, scheduler.Jobs());
            Assert.Equal(occupied ? 2 : 0, host.GetRequiredService<ICapsuleerScene>().MaterializedPilots);
            var live = host.GetRequiredService<LiveWorld>();
            live.MeasureCheckpoints = true;
            await live.FlushAsync();
            var checkpoint = Assert.IsType<WorldCheckpoint>(live.LastCheckpoint);
            output.WriteLine(
                $"Checkpoint capture/projection/write: {checkpoint.Capture.TotalMilliseconds:F2}/"
                    + $"{checkpoint.Projection.TotalMilliseconds:F2}/{checkpoint.Write.TotalMilliseconds:F2} ms."
            );
        }
        finally
        {
            foreach (var lease in leases)
            {
                lease.Dispose();
            }
        }
    }
}
