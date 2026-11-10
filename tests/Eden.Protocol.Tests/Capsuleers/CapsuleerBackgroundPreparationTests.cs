using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerBackgroundPreparationTests
{
    private const int Source = 30000141;
    private const int Third = 31000001;
    private const int Fourth = 31000002;

    [Fact]
    public void RegionsWaitForEntryAndEnteredSystemPrecedesItsNeighbours()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        AddOtherRegion(f.Database);
        PrepareStartup(f);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
        var dormant = preparation.PrepareNext();
        Assert.True(dormant.Complete);
        Assert.Equal(0, dormant.Progress.TotalSystems);
        using (var idle = SessionPerformance.Begin("dormant regions", _ => { }, force: true)!)
        {
            for (int step = 0; step < 100; step++)
            {
                Assert.True(preparation.PrepareNext().Complete);
            }
            Assert.Equal(0, idle.Commands);
            Assert.Equal(0, idle.FittingCalculations);
        }
        var activity = host.GetRequiredService<CapsuleerActivitySource>();
        Guid session = Guid.NewGuid();
        Task changed = preparation.Changed;
        activity.Update(session, Fourth);
        Assert.True(changed.IsCompleted);
        var first = preparation.PrepareNext();
        Assert.Null(first.Failure);
        Assert.Equal(Fourth, first.SystemId);
        Assert.Equal(2, first.Progress.TotalSystems);
        Assert.False(first.Complete);
        var second = preparation.PrepareNext();
        Assert.Null(second.Failure);
        Assert.Equal(Third, second.SystemId);
        Assert.True(second.Complete);
        using WorldDbContext world = new(f.Database.Path);
        Assert.DoesNotContain(world.Set<SavedCapsuleerAgent>(), agent => agent.CommunitySystemId == Source);
        changed = preparation.Changed;
        activity.Update(session, Source);
        Assert.True(changed.IsCompleted);
        var entered = preparation.PrepareNext();
        Assert.Null(entered.Failure);
        Assert.Equal(Source, entered.SystemId);
        Assert.True(entered.Progress.TotalSystems > second.Progress.TotalSystems);
        Assert.Contains(world.Set<SavedCapsuleerAgent>(), agent => agent.CommunitySystemId == Source);
    }

    [Fact]
    public void SavedPilotsRemainDormantUntilTheirRegionIsEntered()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        AddOtherRegion(f.Database);
        PrepareStartup(f);
        WorldInitialization.PrepareCapsuleers(f.Database, f.Clock);
        long[] distant;
        using (WorldDbContext world = new(f.Database.Path))
        {
            distant = world
                .Set<SavedCapsuleerAgent>()
                .Where(agent => agent.CommunitySystemId == Third)
                .Select(agent => agent.PilotId)
                .ToArray();
            Assert.NotEmpty(distant);
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        Assert.Empty(scheduler.Jobs());
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Source);
        Assert.NotEmpty(scheduler.Jobs());
        var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
        while (!preparation.PrepareNext().Complete) { }
        Assert.DoesNotContain(scheduler.Jobs(), job => distant.Contains(job.PilotId));
        f.Clock.Seconds += 120;
        human.Transfer(Third);
        Assert.Contains(scheduler.Jobs(), job => distant.Contains(job.PilotId));
        long elapsed = host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
        Assert.All(scheduler.Jobs().Where(job => distant.Contains(job.PilotId)), job => Assert.True(job.DueTicks > elapsed));
        using WorldDbContext retained = new(f.Database.Path);
        Assert.All(distant, pilot => Assert.Single(retained.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId == pilot)));
    }

    [Fact]
    public void SavedActivationCountsMatchRoutePlansWithoutRequiringRouteSearches()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        AddOtherRegion(f.Database);
        using CatalogDbContext catalog = new(f.Database.CatalogPath);
        foreach (int system in catalog.Definitions.Systems.Keys)
        {
            var geography = CapsuleerTrafficRoutes.Geography(catalog, system);
            var route = CapsuleerTrafficRoutes.Read(catalog, system);
            Assert.Equal(route.GateCount, geography.GateCount);
            Assert.Equal(route.StationCount, geography.StationCount);
        }
    }

    [Fact]
    public void LoginsDoNotWaitForGlobalCreationAndOccupiedSystemsArePreparedFirst()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        PrepareStartup(f);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Empty(world.Set<SavedCapsuleerAgent>());
            Assert.Empty(world.Set<SavedCapsuleerJob>());
        }
        var synchronization = host.GetRequiredService<IWorldSynchronization>();
        host.GetRequiredService<CapsuleerActivitySource>().Update(Guid.NewGuid(), Third);
        var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
        int templates = 0;
        using (
            var metrics = SessionPerformance.Begin(
                "background preparation",
                line =>
                {
                    if (line.StartsWith("Performance startup.shipBlueprint:", StringComparison.Ordinal))
                    {
                        Assert.False(synchronization.IsEntered);
                        templates++;
                    }
                },
                force: true
            )!
        )
        {
            var first = preparation.PrepareNext();
            Assert.Null(first.Failure);
            Assert.Equal(Third, first.SystemId);
            Assert.False(first.Complete);
        }
        Assert.True(templates > 0);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Contains(saved.Set<SavedCapsuleerAgent>(), agent => agent.CommunitySystemId == Third);
        Assert.DoesNotContain(saved.Set<SavedCapsuleerAgent>(), agent => agent.CommunitySystemId == Source);
        Assert.NotEmpty(host.GetRequiredService<ICapsuleerScheduler>().Jobs());
        CapsuleerBackgroundTurn turn;
        do
        {
            turn = preparation.PrepareNext();
            Assert.Null(turn.Failure);
        } while (!turn.Complete);
        Assert.Equal(turn.Progress.TotalSystems, turn.Progress.CompletedSystems);
        Assert.Equal(0, turn.Progress.DeferredSystems);
        for (int second = 0; second < 35; second++)
        {
            f.Clock.Seconds++;
            host.GetRequiredService<CapsuleerMaintenance>().Tick();
        }
        Assert.Contains(host.GetRequiredService<ICapsuleerScheduler>().Jobs(), job => job.Sequence > 0);
        using var idle = SessionPerformance.Begin("completed population queue", _ => { }, force: true)!;
        for (int index = 0; index < 100; index++)
        {
            Assert.True(preparation.PrepareNext().Complete);
        }
        Assert.Equal(0, idle.Commands);
        Assert.Equal(0, idle.Connections);
        Assert.Equal(0, idle.FittingCalculations);
    }

    [Fact]
    public void FailedCommunityRollsBackWhileOthersFinishAndRestartRetriesOnlyPendingWork()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        CapsuleerProvisioningTests.AddSystem(f.Database, Third);
        PrepareStartup(f);
        long[] retained;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            host.GetRequiredService<CapsuleerActivitySource>().Update(Guid.NewGuid(), Source);
            using (WorldDbContext setup = new(f.Database.Path))
            {
                setup.Database.ExecuteSqlRaw(
                    """
                    CREATE TRIGGER reject_background BEFORE INSERT ON WorldCapsuleerAgent
                    WHEN NEW.CommunitySystemId=30000141 BEGIN SELECT RAISE(ABORT,'background interrupted'); END;
                    """
                );
            }
            var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
            var failed = preparation.PrepareNext();
            Assert.Equal(Source, failed.SystemId);
            Assert.NotNull(failed.Failure);
            Assert.Equal(1, failed.Progress.DeferredSystems);
            CapsuleerBackgroundTurn turn;
            do
            {
                turn = preparation.PrepareNext();
            } while (!turn.Complete);
            using WorldDbContext world = new(f.Database.Path);
            Assert.DoesNotContain(world.Set<SavedCapsuleerAgent>(), agent => agent.CommunitySystemId == Source);
            retained = world.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).Select(agent => agent.PilotId).ToArray();
            Assert.NotEmpty(retained);
            world.Database.ExecuteSqlRaw("DROP TRIGGER reject_background");
        }
        WorldInitialization.Initialize(f.Database, f.Clock);
        WorldInitialization.PrepareCapsuleers(f.Database, f.Clock, maximumCommunities: 0);
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            host.GetRequiredService<CapsuleerActivitySource>().Update(Guid.NewGuid(), Source);
            var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
            var turn = preparation.PrepareNext();
            Assert.Equal(Source, turn.SystemId);
            Assert.Null(turn.Failure);
            while (!turn.Complete)
            {
                turn = preparation.PrepareNext();
                Assert.Null(turn.Failure);
            }
            using WorldDbContext world = new(f.Database.Path);
            Assert.Contains(world.Set<SavedCapsuleerAgent>(), agent => agent.CommunitySystemId == Source);
            Assert.All(retained, pilot => Assert.Single(world.Set<SavedCapsuleerAgent>().Where(agent => agent.PilotId == pilot)));
        }
    }

    [Fact]
    public void CancellationDuringMemoryValidationPublishesNoPilotAndRetryResumes()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        PrepareStartup(f);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
        host.GetRequiredService<CapsuleerActivitySource>().Update(Guid.NewGuid(), Source);
        using CancellationTokenSource cancellation = new();
        using (
            var metrics = SessionPerformance.Begin(
                "cancel memory preparation",
                line =>
                {
                    if (line.StartsWith("Performance startup.shipBlueprint:", StringComparison.Ordinal))
                    {
                        cancellation.Cancel();
                    }
                },
                force: true
            )!
        )
        {
            Assert.Throws<OperationCanceledException>(() => preparation.PrepareNext(cancellation.Token));
        }
        using WorldDbContext world = new(f.Database.Path);
        Assert.Empty(world.Set<SavedCapsuleerAgent>());
        Assert.Empty(world.Set<SavedCapsuleerJob>());
        var retried = preparation.PrepareNext();
        Assert.Equal(Source, retried.SystemId);
        Assert.Null(retried.Failure);
        Assert.NotEmpty(world.Set<SavedCapsuleerAgent>());
    }

    [Fact]
    public void WorkerBoundsEachTurnAndCancellationStartsNoCommunity()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false, isolateRoute: true);
        using var host = ServerServices.Build(f.Database, f.Clock);
        FakePreparation preparation = new();
        using CapsuleerPopulationMaintenance worker = new(preparation, f.Clock, host.GetRequiredService<IPilotOperationScope>());
        Assert.False(worker.Tick());
        Assert.Equal(16, preparation.Calls);
        Assert.True(worker.Tick());
        Assert.Equal(18, preparation.Calls);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => worker.Tick(cancellation.Token));
        Assert.Equal(18, preparation.Calls);
    }

    private static void PrepareStartup(CapsuleerHaulerTests.Fixture f)
    {
        var settings = new WorldConfigurationStore(f.Database, f.Clock);
        var state = settings.Read();
        settings.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with { Capsuleers = new(Miners: false, TrafficMultiplier: 2, GateVisitors: 0) },
            },
            "background test"
        );
        WorldInitialization.Initialize(f.Database, f.Clock);
        using var metrics = SessionPerformance.Begin("login population preparation", _ => { }, force: true)!;
        var ready = WorldInitialization.PrepareCapsuleers(f.Database, f.Clock, maximumCommunities: 0);
        Assert.Equal(0, ready.CreatedCommunities);
        Assert.True(ready.PendingCommunities > 0);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    private static void AddOtherRegion(WorldDatabase database)
    {
        CapsuleerProvisioningTests.AddSystem(database, Third);
        CapsuleerProvisioningTests.AddSystem(database, Fourth);
        using CatalogDbContext catalog = new(database.CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Region(Id,Name) VALUES(10000043,'Dormant region');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES(22000001,10000043,'Dormant constellation');
            UPDATE SolarSystem SET ConstellationId=22000001 WHERE Id IN(31000001,31000002);
            """
        );
    }

    private sealed class FakePreparation : ICapsuleerBackgroundPreparation
    {
        internal int Calls { get; private set; }
        public Task Changed { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public CapsuleerBackgroundTurn PrepareNext(CancellationToken cancellationToken = default)
        {
            Calls++;
            return new(new(Calls, 18, 0), Calls, Calls == 18);
        }
    }
}
