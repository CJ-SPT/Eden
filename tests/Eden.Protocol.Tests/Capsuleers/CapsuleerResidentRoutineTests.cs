using Eden.Data.World.Capsuleers;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerResidentRoutineTests
{
    private const int Home = 30000141;
    private const int Remote = 30000144;
    private const long Human = 90000001;

    [Fact]
    public async Task AsyncTransactionProjectsNewResidentPilotBeforeRoutineRows()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: true, migrateRoutineTables: true);
        long pilot;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var live = host.GetRequiredService<LiveWorld>();
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
            using CapsuleerProvisioner provisioner = new(fixture.Database, fixture.Clock);
            using (live.EnterShared())
            {
                using (var action = live.Economy.Begin(new(systems: [Home])))
                {
                    pilot = Assert.IsType<long>(provisioner.Provision(Home, catalog, action, maximumFounders: 1).CreatedPilotId);
                    action.Commit();
                }
                live.CapsuleerRoutines.Admit(pilot);
                live.CapsuleerRoutines.Settle(pilot, TimeSpan.TicksPerSecond);
            }
            host.GetRequiredService<CapsuleerScheduler>().PopulationPrepared(Home, pilot);

            using (WorldDbContext saved = new(fixture.Database.Path) { IsResidentProjection = true })
            {
                Assert.Null(saved.Pilots.Find(pilot));
            }
            using (WorldDbContext world = new(fixture.Database.Path) { Participants = new(pilots: [pilot]) })
            {
                using var transaction = await world.Database.BeginTransactionAsync();
                Assert.NotNull(world.Pilots.Find(pilot));
                Assert.NotNull(world.Set<SavedCapsuleerAdmission>().Find(pilot));
                Assert.Equal(TimeSpan.TicksPerSecond, world.Set<SavedCapsuleerCoarsePilot>().Find(pilot)!.SettledTicks);
                await transaction.CommitAsync();
            }
            await live.FlushAsync();
        }
        using WorldDbContext reopened = new(fixture.Database.Path);
        Assert.NotNull(reopened.Pilots.Find(pilot));
        Assert.NotNull(reopened.Set<SavedCapsuleerAdmission>().Find(pilot));
        Assert.Equal(TimeSpan.TicksPerSecond, reopened.Set<SavedCapsuleerCoarsePilot>().Find(pilot)!.SettledTicks);
    }

    [Fact]
    public void RoutineCursorAndPositionStayResidentAndDetachedCaptureKeepsOneGeneration()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
        scheduler.Run(0);
        var live = host.GetRequiredService<LiveWorld>();
        var original = scheduler.Jobs().First();
        long ship;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            ship = world.Pilots.Where(p => p.Id == original.PilotId).Select(p => p.ActiveShipId).Single();
        }
        var runtime = live.CombatForSystem(Home)!;
        Assert.True(live.ReleaseSystemDetails(Home));
        var cursor = new CapsuleerCoarseWork(Home, 0, 0, 0, 0) { ThroughTicks = TimeSpan.TicksPerSecond };
        var next = original.Next(TimeSpan.TicksPerHour, original.Action);
        SpacePosition first = new(1, 2, 3);
        using (live.EnterShared())
        using (var metrics = SessionPerformance.Begin("resident routine and motion", _ => { }, force: true)!)
        {
            live.CapsuleerRoutines.Publish(cursor);
            using CapsuleerOperation operation = new(fixture.Database, original, host.GetRequiredService<MarketChangeSignal>(), cursor);
            operation.Commit(next, null, null);
            runtime.PublishPilotPosition(ship, Home, first);
            Assert.True(live.ReleaseSystemDetails(Home));
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        using var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        var newer = next.Next(TimeSpan.TicksPerHour * 2, next.Action);
        SpacePosition second = new(4, 5, 6);
        using (live.EnterShared())
        {
            var current = live.CapsuleerRoutines.Work[Home] with { ThroughTicks = TimeSpan.TicksPerSecond * 2 };
            using CapsuleerOperation operation = new(fixture.Database, next, host.GetRequiredService<MarketChangeSignal>(), current);
            operation.Commit(newer, null, null);
            runtime.PublishPilotPosition(ship, Home, second);
        }
        captured.Project();
        using (WorldDbContext snapshot = new(captured.Database))
        {
            Assert.Equal(next.Revision, snapshot.Set<SavedCapsuleerJob>().Single(j => j.PilotId == original.PilotId).Revision);
            Assert.Equal(1, snapshot.Set<SavedCapsuleerCoarseWork>().Single().Generation);
            Assert.Equal(TimeSpan.TicksPerSecond, snapshot.Set<SavedCapsuleerCoarsePilot>().Single().SettledTicks);
            Assert.Equal(first.X, snapshot.Positions.Single(p => p.ShipId == ship).X);
        }
        Assert.Equal(newer, live.CapsuleerJobs[original.PilotId]);
        Assert.Equal(2, live.CapsuleerRoutines.Work[Home].Generation);
        Assert.Equal(second, runtime.InformationPosition(ship));
        using var latest = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot());
        latest.Project();
        using WorldDbContext projected = new(latest.Database);
        Assert.Equal(newer.Revision, projected.Set<SavedCapsuleerJob>().Single(j => j.PilotId == original.PilotId).Revision);
        Assert.Equal(2, projected.Set<SavedCapsuleerCoarseWork>().Single().Generation);
        Assert.Equal(second.X, projected.Positions.Single(p => p.ShipId == ship).X);
    }

    [Fact]
    public async Task FailedRoutineProjectionRetainsTheCursorAndJobForRetryAndReopen()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        CapsuleerJob next;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
            scheduler.Run(0);
            var live = host.GetRequiredService<LiveWorld>();
            var original = scheduler.Jobs().First();
            next = original.Next(TimeSpan.TicksPerHour, original.Action);
            fixture.Clock.Milliseconds += 1000;
            using (live.EnterShared())
            {
                var work = new CapsuleerCoarseWork(Home, 0, 0, 0, 0) { ThroughTicks = TimeSpan.TicksPerSecond };
                live.CapsuleerRoutines.Publish(work);
                using CapsuleerOperation operation = new(fixture.Database, original, host.GetRequiredService<MarketChangeSignal>(), work);
                operation.Commit(next, null, null);
            }
            using (var captured = Assert.IsType<WorldSnapshot>(live.CaptureSnapshot()))
            {
                using (var command = captured.Database.CreateCommand())
                {
                    command.CommandText = """
                        CREATE TRIGGER RejectCursor BEFORE INSERT ON WorldCapsuleerCoarseWork
                        BEGIN SELECT RAISE(ABORT,'test cursor projection'); END
                        """;
                    command.ExecuteNonQuery();
                }
                Assert.Throws<SqliteException>(() => captured.Project());
                using WorldDbContext snapshot = new(captured.Database);
                Assert.Equal(original.Revision, snapshot.Set<SavedCapsuleerJob>().Single(j => j.PilotId == original.PilotId).Revision);
                Assert.Empty(snapshot.Set<SavedCapsuleerCoarseWork>());
            }
            Assert.Single(live.CapsuleerRoutines.Capture().Work);
            Assert.Equal(next, live.CapsuleerJobs[original.PilotId]);
            await live.FlushAsync();
            // Successful detached saves must also retain differences from the live SQL image.
            Assert.Single(live.CapsuleerRoutines.Capture().Work);
            live.ObjectsChanged();
            await live.FlushAsync();
        }
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var returningHuman = reopened.GetRequiredService<IHumanPresence>().Select(Human, Home);
        var restored = reopened.GetRequiredService<CapsuleerScheduler>();
        Assert.Contains(next, restored.Jobs());
        var resident = reopened.GetRequiredService<LiveWorld>();
        Assert.Equal(1, resident.CapsuleerRoutines.Work[Home].Generation);
        Assert.Equal(TimeSpan.TicksPerSecond, resident.CapsuleerRoutines.Settled[next.PilotId]);
    }

    [Fact]
    public async Task ClassifiedRoutineQueryProjectsOnlyItsParticipatingRoutine()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.Run(0);
        var live = host.GetRequiredService<LiveWorld>();
        var original = scheduler.Jobs().ToArray();
        Assert.Equal(2, original.Length);
        using (live.EnterShared())
        {
            foreach (var job in original)
            {
                using CapsuleerOperation operation = new(fixture.Database, job, host.GetRequiredService<MarketChangeSignal>());
                operation.Commit(job.Next(TimeSpan.TicksPerHour, job.Action), null, null);
            }
        }
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        var remote = Task.Run(() =>
        {
            using var ownership = live.Enter("unrelated resident owner", Remote);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            await Task.Run(() =>
                {
                    using WorldDbContext world = new(fixture.Database.Path) { Participants = new(pilots: [original[0].PilotId]) };
                    using var transaction = world.Database.BeginTransaction();
                    Assert.Equal(original[0], live.ProjectedCapsuleerJobs[original[0].PilotId]);
                    Assert.Equal(
                        original[0].Revision + 1,
                        world.Set<SavedCapsuleerJob>().Single(j => j.PilotId == original[0].PilotId).Revision
                    );
                    transaction.Commit();
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();
            await remote;
        }
        Assert.Equal(original[0].Revision + 1, live.ProjectedCapsuleerJobs[original[0].PilotId].Revision);
        Assert.Equal(original[1], live.ProjectedCapsuleerJobs[original[1].PilotId]);
        using WorldDbContext saved = new(fixture.Database.Path) { IsResidentProjection = true };
        Assert.Equal(original[0].Revision + 1, saved.Set<SavedCapsuleerJob>().Single(j => j.PilotId == original[0].PilotId).Revision);
        Assert.Equal(original[1].Revision, saved.Set<SavedCapsuleerJob>().Single(j => j.PilotId == original[1].PilotId).Revision);
    }

    [Fact]
    public void StaleResidentCursorRejectsAnEconomicCommitWithoutPublishingProgress()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Home);
        host.GetRequiredService<CapsuleerScheduler>().Run(0);
        var original = host.GetRequiredService<CapsuleerScheduler>().Jobs().First();
        using var ownership = live.EnterShared();
        var before = new CapsuleerCoarseWork(Home, 0, 0, 0, 0);
        live.CapsuleerRoutines.Publish(before);
        using CapsuleerOperation operation = new(fixture.Database, original, host.GetRequiredService<MarketChangeSignal>(), before);
        var wallet = operation.World.Set<PilotWallet>().Find(original.PilotId)!;
        long cents = wallet.Cents;
        wallet.Cents--;
        operation.World.SaveChanges();
        live.CapsuleerRoutines.Publish(before with { Generation = 1 });
        Assert.Throws<CapsuleerCoarseConflictException>(() => operation.Commit(original.Next(1, original.Action), null, null));
        operation.Dispose();
        Assert.Equal(original, live.CapsuleerJobs[original.PilotId]);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(cents, saved.Set<PilotWallet>().Find(original.PilotId)!.Cents);
    }
}
