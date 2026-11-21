using Eden.Data.World.Economy;
using Eden.Data.World.Market;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class TrafficPreparationTests
{
    private const int SourceSystem = 30000141;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(1024)]
    [InlineData(30000)]
    public void GeneratedNameRecognitionPreservesTheExactCollisionAttemptBound(int attempt)
    {
        const long Seed = 912345;
        const int Slot = 12;
        var career = CapsuleerCareer.Hauling;
        string name = CapsuleerProvisioningRules.Name(Seed, SourceSystem, career, Slot, attempt);
        Assert.True(CapsuleerProvisioningRules.MatchesName(Seed, SourceSystem, career, Slot, name, attempt + 1));
        Assert.False(CapsuleerProvisioningRules.MatchesName(Seed, SourceSystem, career, Slot, name, attempt));
        Assert.False(CapsuleerProvisioningRules.MatchesName(Seed, SourceSystem, career, Slot, "A custom pilot", attempt + 1));
    }

    [Fact]
    public void PreparingANewBeltDoesNotInvalidateUnrelatedCommittedGeometry()
    {
        const int OtherSystem = 30000142;
        const int SourceBelt = 40000001;
        const int OtherBelt = 40000002;
        const long Catalog = 1;
        LiveTrafficGeometry geometry = new();
        geometry.Publish(SourceSystem, Catalog, [SourceBelt], []);
        Assert.True(geometry.TryRead(SourceSystem, Catalog, out var before));
        geometry.TrackBelts(OtherSystem, [OtherBelt]);
        long asteroid =
            Eden.Gameplay.Mining.AsteroidDefinitions.FirstAsteroidId
            + (long)OtherBelt * Eden.Gameplay.Mining.AsteroidDefinitions.IdsPerBelt;
        geometry.Observe([new("WorldAsteroid", asteroid)]);
        Assert.True(geometry.TryRead(SourceSystem, Catalog, out var after));
        Assert.Same(before, after);
    }

    [Fact]
    public void ImmutableActivityClockKeepsFractionalTicksAcrossPauseAndResume()
    {
        const long Frequency = 3;
        HumanActivity activity = new(0, 0);
        Guid session = Guid.NewGuid();
        activity.Update(session, SourceSystem, 0, Frequency);
        _ = activity.Read(1, Frequency);
        var first = activity.CaptureClock();
        Assert.Equal(activity.Read(2, Frequency).ElapsedTicks, first.ElapsedAt(2));
        activity.Update(session, null, 2, Frequency);
        var paused = activity.CaptureClock();
        Assert.Equal(paused.ElapsedTicks, paused.ElapsedAt(10));
        activity.Update(session, SourceSystem, 10, Frequency);
        var resumed = activity.CaptureClock();
        Assert.Equal(TimeSpan.TicksPerSecond, resumed.ElapsedAt(11));
        Assert.Equal(activity.Read(11, Frequency).ElapsedTicks, resumed.ElapsedAt(11));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedJobCannotUseItsEarlierPreparationAgain(bool testingMarket)
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Set<SavedWorldEconomy>().Find(WorldEconomySettings.SingletonId)!.TestingMarket = testingMarket;
            world.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var live = host.GetRequiredService<LiveWorld>();
        fixture.Clock.Seconds += 1;
        var input = Assert.IsType<CapsuleerScheduler.PreparationInput>(scheduler.CapturePreparation());
        Assert.NotEqual(0, input.StationId);
        Assert.Equal(!testingMarket, input.PrepareInstitutionalOffers);
        using (live.EnterShared())
        {
            Assert.True(scheduler.PreparationCurrent(input));
        }
        var turn = scheduler.Run(1, maximumActions: 1);
        Assert.Equal(1, turn.Turn.Completed);
        using (live.EnterShared())
        {
            Assert.False(scheduler.PreparationCurrent(input));
            Assert.Equal(input.Job.Revision + 1, live.CapsuleerJobs[input.Job.PilotId].Revision);
        }
    }

    [Fact]
    public void CancelledPreparationLeavesItsJobDueWithoutRecordingAFailure()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        fixture.Clock.Seconds++;
        var before = scheduler.Jobs();
        using CancellationTokenSource shutdown = new();
        using (
            SessionPerformance.Capture(
                "shutdown during preparation",
                sample =>
                {
                    if (sample.Name == "traffic.prepareCapture")
                    {
                        shutdown.Cancel();
                    }
                }
            )
        )
        {
            Assert.Throws<OperationCanceledException>(() => scheduler.Run(1, 1, shutdown.Token));
        }
        Assert.Equal(before, scheduler.Jobs());
        Assert.Equal(0, scheduler.Read().Failures);
        Assert.Equal(1, scheduler.Run(1, 1).Turn.Completed);
    }

    [Fact]
    public void CommittedFittingRevisionDiscardsPreparationWithoutAdvancingTheJob()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, SourceSystem);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var live = host.GetRequiredService<LiveWorld>();
        fixture.Clock.Seconds += 1;
        var input = Assert.IsType<CapsuleerScheduler.PreparationInput>(scheduler.CapturePreparation());
        using (WorldDbContext world = new(fixture.Database.Path) { Participants = new(pilots: [input.Job.PilotId]) })
        {
            using var transaction = world.Database.BeginTransaction();
            world.Database.ExecuteSql($"UPDATE WorldFitRevision SET Stamp=Stamp+1 WHERE PilotId={input.Job.PilotId}");
            transaction.Rollback();
        }
        using (live.EnterShared())
        {
            Assert.True(scheduler.PreparationCurrent(input));
        }
        using (WorldDbContext world = new(fixture.Database.Path) { Participants = new(pilots: [input.Job.PilotId]) })
        {
            Assert.Equal(1, world.Database.ExecuteSql($"UPDATE WorldFitRevision SET Stamp=Stamp+1 WHERE PilotId={input.Job.PilotId}"));
        }
        using (live.EnterShared())
        {
            Assert.False(scheduler.PreparationCurrent(input));
            Assert.Equal(input.Job, live.CapsuleerJobs[input.Job.PilotId]);
        }
    }

    [Fact]
    public void CommittedGeometryIsReusedInsideTransactionsButPendingDepletionIsPrivate()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using var owner = live.EnterShared();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        using (WorldDbContext setup = new(fixture.Database.Path) { Participants = new(systems: [SourceSystem]) })
        {
            using var transaction = setup.Database.BeginTransaction();
            AsteroidFieldStore.Populate(setup, catalog, SourceSystem, fixture.Clock.Now);
            setup.SaveChanges();
            transaction.Commit();
        }
        using (WorldDbContext capture = new(fixture.Database.Path) { Participants = TransactionParticipants.None })
        {
            Assert.Empty(CapsuleerGeometry.Capture(fixture.Database, capture, catalog, SourceSystem));
        }
        Assert.True(live.TrafficGeometry.TryRead(SourceSystem, catalog.Definitions.Identity.Generation, out var committed));
        long depleted;
        using (WorldDbContext world = new(fixture.Database.Path) { Participants = new(systems: [SourceSystem]) })
        using (var transaction = world.Database.BeginTransaction())
        {
            using (var metrics = SessionPerformance.Begin("warm transaction geometry", _ => { }, force: true)!)
            {
                Assert.Same(committed, CapsuleerGeometry.Load(world, catalog, SourceSystem, fixture.Clock.Now));
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            depleted = world.Set<WorldAsteroid>().Where(a => a.SystemId == SourceSystem && a.Quantity > 0).Select(a => a.Id).First();
            world.Set<WorldAsteroid>().Where(a => a.Id == depleted).ExecuteUpdate(s => s.SetProperty(a => a.Quantity, 0));
            var pending = CapsuleerGeometry.Load(world, catalog, SourceSystem, fixture.Clock.Now);
            Assert.DoesNotContain(pending, item => item.Id == depleted);
            Assert.Contains(committed, item => item.Id == depleted);
            transaction.Rollback();
        }
        using WorldDbContext after = new(fixture.Database.Path) { Participants = TransactionParticipants.None };
        Assert.Same(committed, CapsuleerGeometry.Load(after, catalog, SourceSystem, fixture.Clock.Now));
        Assert.True(after.Set<WorldAsteroid>().Single(a => a.Id == depleted).Quantity > 0);
    }

    [Fact]
    public void PartialMiningKeepsAsteroidContactsWarmAndDepletionRefreshesOnlyTheMinedSystem()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using var owner = live.EnterShared();
        var field = host.GetRequiredService<Eden.Gameplay.Mining.IAsteroidField>();
        var local = field.List(SourceSystem);
        long rock = local.First().Id;
        var remote = field.List(SourceSystem + 1);
        void SetQuantity(long quantity)
        {
            using var work = live.Economy.Begin(new(systems: [SourceSystem]));
            work.Set<WorldAsteroid>().Find(rock)!.Quantity = quantity;
            work.Commit();
        }
        SetQuantity(1);
        Assert.Same(local, field.List(SourceSystem));
        SetQuantity(0);
        Assert.DoesNotContain(field.List(SourceSystem), item => item.Id == rock);
        Assert.Same(remote, field.List(SourceSystem + 1));
        SetQuantity(2);
        Assert.Contains(field.List(SourceSystem), item => item.Id == rock);
    }

    [Fact]
    public void ResidentGeometryReadsIndexedSnapshotsAndSeesPrivateDepletionAndRenewal()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var live = host.GetRequiredService<LiveWorld>();
        using var owner = live.EnterShared();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        using (var setup = live.Economy.Begin(new(systems: [SourceSystem])))
        {
            AsteroidFieldStore.Populate(setup, catalog, SourceSystem, fixture.Clock.Now);
            setup.Commit();
        }
        long asteroid;
        int belt;
        using (var read = live.Economy.Begin(new(systems: [SourceSystem])))
        {
            var resources = CapsuleerGeometry.CaptureResources(fixture.Database, read, catalog, SourceSystem)!;
            Assert.NotNull(resources.Resources);
            var first = resources.Resources.First();
            asteroid = first.Id;
            belt = first.AsteroidBeltId!.Value;
            using var metrics = SessionPerformance.Begin("resident asteroid geometry", _ => { }, force: true)!;
            var warm = CapsuleerGeometry.CaptureResources(fixture.Database, read, catalog, SourceSystem)!;
            Assert.Equal(resources.Resources, warm.Resources);
            Assert.Equal(0, read.Materializations);
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
            read.Set<WorldAsteroid>().Find(asteroid)!.Quantity = 0;
            var depleted = CapsuleerGeometry.CaptureResources(fixture.Database, read, catalog, SourceSystem)!;
            Assert.Empty(depleted.Missing);
            Assert.DoesNotContain(depleted.Resources!, item => item.Id == asteroid);
            var renewals = read.Set<SavedBeltRenewal>();
            read.Remove(renewals.Find(belt)!);
            Assert.Null(CapsuleerGeometry.CaptureResources(fixture.Database, read, catalog, SourceSystem)!.Resources);
        }
        using var after = live.Economy.Begin(new(systems: [SourceSystem]));
        var restored = CapsuleerGeometry.CaptureResources(fixture.Database, after, catalog, SourceSystem)!;
        Assert.Contains(restored.Resources!, item => item.Id == asteroid);
        Assert.Equal(0, after.Materializations);
    }

    [Fact]
    public void PreparedAsteroidsNeverReplenishAnExistingDepletedDeposit()
    {
        using CapsuleerHaulerTests.Fixture fixture = new();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        using WorldDbContext world = new(fixture.Database.Path);
        AsteroidFieldStore.Populate(world, catalog, SourceSystem, fixture.Clock.Now);
        world.SaveChanges();
        var rock = world.Set<WorldAsteroid>().First(a => a.SystemId == SourceSystem);
        var prepared = AsteroidFieldStore.Prepare(catalog, rock.BeltId);
        rock.Quantity = 0;
        world.SaveChanges();
        using var scope = AsteroidFieldStore.UsePrepared(new Dictionary<int, AsteroidFieldStore.PreparedBelt> { [rock.BeltId] = prepared });
        using var transaction = world.Database.BeginTransaction();
        AsteroidFieldStore.Populate(world, catalog, SourceSystem, fixture.Clock.Now);
        world.SaveChanges();
        transaction.Commit();
        world.ChangeTracker.Clear();
        Assert.Equal(0, world.Set<WorldAsteroid>().Single(a => a.Id == rock.Id).Quantity);
    }
}
