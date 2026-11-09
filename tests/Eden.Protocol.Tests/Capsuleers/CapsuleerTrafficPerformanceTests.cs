using System.Diagnostics;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.Server.Chat;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void DueMaintenanceScopesCatalogReadsWithoutChangingJobs()
    {
        (int Connections, CapsuleerJob[] Jobs) Measure(bool scoped)
        {
            using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
            using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
            using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
            using var worker = new CapsuleerMaintenance(
                host.GetRequiredService<ICapsuleerScheduler>(),
                host.GetRequiredService<ICapsuleerHandoff>(),
                new CountingScene(
                    host.GetRequiredService<ICapsuleerScene>(),
                    () => Assert.Equal(scoped, CatalogConnectionScope.Inputs(f.Database.CatalogPath).IsScoped)
                ),
                host.GetRequiredService<ChatRuntime>(),
                f.Clock,
                host.GetRequiredService<IWorldSynchronization>(),
                host.GetRequiredService<ICapsuleerUpdateDiagnostics>(),
                scoped ? host.GetRequiredService<IPilotOperationScope>() : null
            );
            using var metrics = SessionPerformance.Begin($"maintenance scoped={scoped}", output.WriteLine, force: true)!;
            for (int step = 0; step < 12; step++)
            {
                f.Clock.Seconds++;
                worker.Tick();
                Assert.False(CatalogConnectionScope.Inputs(f.Database.CatalogPath).IsScoped);
            }
            return (metrics.Connections, host.GetRequiredService<ICapsuleerScheduler>().Jobs().ToArray());
        }
        var baseline = Measure(false);
        var scoped = Measure(true);
        Assert.Equal(baseline.Jobs, scoped.Jobs);
        Assert.True(scoped.Connections <= baseline.Connections);
        output.WriteLine($"Due maintenance connections unscoped/scoped: {baseline.Connections}/{scoped.Connections}.");
    }

    [Fact]
    public void CaptureReflectsSameTickDamageAndSubsecondRechargeWithoutAnotherTrafficTurn()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, 30000141);
        host.GetRequiredService<CapsuleerMaintenance>().Tick();
        var handoff = host.GetRequiredService<ICapsuleerHandoff>();
        var before = handoff.Capture();
        var miner = Assert.Single(before.Miners);
        var health = Assert.IsType<ShipHealth>(miner.Health);
        using (WorldDbContext world = new(f.Database.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            PilotHealthStore.Apply(world, miner.Pilot.ShipId, health.Defense, new DamageVector(1, 0, 0, 0), f.Clock.Now);
            world.SaveChanges();
            transaction.Commit();
        }
        var damaged = handoff.Capture();
        Assert.True(damaged.Revision > before.Revision);
        double shield = Assert.Single(damaged.Miners).Health!.Remaining.Shield;
        Assert.True(shield < health.Remaining.Shield);
        Assert.Same(damaged, handoff.Capture());
        f.Clock.Milliseconds += 100;
        var recharged = handoff.Capture();
        Assert.True(recharged.Revision > damaged.Revision);
        Assert.True(Assert.Single(recharged.Miners).Health!.Remaining.Shield > shield);
        Assert.Equal(miner.Work, Assert.Single(recharged.Miners).Work);
    }

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
            var scene = new CountingScene(host.GetRequiredService<ICapsuleerScene>());
            using var worker = new CapsuleerMaintenance(
                host.GetRequiredService<ICapsuleerScheduler>(),
                host.GetRequiredService<ICapsuleerHandoff>(),
                scene,
                host.GetRequiredService<ChatRuntime>(),
                f.Clock,
                host.GetRequiredService<IWorldSynchronization>(),
                host.GetRequiredService<ICapsuleerUpdateDiagnostics>(),
                host.GetRequiredService<IPilotOperationScope>()
            );
            var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
            worker.Tick();
            var before = scheduler.Jobs().ToArray();
            var handoff = host.GetRequiredService<ICapsuleerHandoff>();
            _ = handoff.Capture();
            var published = handoff.Capture();
            Assert.Same(published, handoff.Capture());
            int publications = scene.Applications;
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
            Assert.Equal(publications, scene.Applications);
            Assert.Equal(before, scheduler.Jobs());
            Assert.Same(published, handoff.Capture());
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

    private sealed class CountingScene(ICapsuleerScene actual, Action? inspect = null) : ICapsuleerScene
    {
        public int Applications { get; private set; }
        public int Stamp => actual.Stamp;

        public void Publish(MinerObservation observation) => actual.Publish(observation);

        public void Publish(HaulerObservation observation) => actual.Publish(observation);

        public void Apply(CapsuleerBatch batch)
        {
            inspect?.Invoke();
            Applications++;
            actual.Apply(batch);
        }
    }
}
