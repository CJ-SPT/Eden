using Eden.Data.World.Capsuleers;
using Eden.Data.World.Mining;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Server;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerCoarseTests(ITestOutputHelper output)
{
    private const int Home = 30000141;
    private const int Elsewhere = 30000144;

    [Theory]
    [InlineData(0)]
    [InlineData(180)]
    public void RemotePaidMiningReusesValidationWithinItsTransaction(int availableSeconds)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        MinerObservation? paid = null;
        for (int action = 0; action < 1000; action++)
        {
            paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
            if (paid.Work.Activity == MinerActivity.Mining)
            {
                break;
            }
        }
        Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
        fixture.Clock.Seconds += 180;
        using WorldDbContext world = new(fixture.Database.Path) { Participants = new(pilots: [paid.Pilot.PilotId]) };
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(fixture.Database.CatalogPath);
        long before = world.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity;
        using var metrics = SessionPerformance.Begin("remote mining validation", _ => { }, force: true)!;
        var result = PilotMiningStore.CollectRemote(
            world,
            catalog,
            paid.Pilot.PilotId,
            paid.Work.ModuleId,
            paid.Work.TargetId,
            TimeSpan.FromSeconds(availableSeconds).Ticks,
            new(.5, .5, .5, .5, .5),
            fixture.Clock.Now
        );
        output.WriteLine($"Remote mining: SQL={metrics.Commands}, connections={metrics.Connections}, fits={metrics.FittingCalculations}");
        Assert.InRange(metrics.Commands, 1, 64);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
        Assert.Equal(availableSeconds > 0, result.Cycles > 1);
        long remaining = world.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity;
        Assert.True(remaining < before);
        Assert.Throws<InvalidDataException>(() =>
            PilotMiningStore.CollectRemote(
                world,
                catalog,
                paid.Pilot.PilotId,
                paid.Work.ModuleId,
                paid.Work.TargetId,
                0,
                new(.5, .5, .5, .5, .5),
                fixture.Clock.Now
            )
        );
        Assert.Equal(remaining, world.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity);
        transaction.Rollback();
        using WorldDbContext reopened = new(fixture.Database.Path) { Participants = TransactionParticipants.None };
        Assert.Equal(before, reopened.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity);
    }

    [Fact]
    public void RemoteHaulerNavigationDoesNotLoadAsteroidResources()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        long pilot = fixture.Founder().PilotId;
        var previous = scheduler.Jobs().Single(job => job.PilotId == pilot);
        fixture.Clock.Seconds += 180;
        for (int turn = 0; turn < CapsuleerCoarseRules.PageSize; turn++)
        {
            List<string> commands = [];
            using var operation = host.GetRequiredService<IPilotOperationScope>().Begin();
            using var metrics = SessionPerformance.Begin("remote hauler geometry", _ => { }, force: true, command: commands.Add)!;
            scheduler.Run(checked((int)fixture.Clock.Seconds));
            if (scheduler.Jobs().Single(job => job.PilotId == pilot).Revision > previous.Revision)
            {
                output.WriteLine(
                    $"Remote hauler: SQL={metrics.Commands}, connections={metrics.Connections}, fits={metrics.FittingCalculations}"
                );
                Assert.DoesNotContain(commands, sql => sql.Contains("WorldAsteroid", StringComparison.Ordinal));
                Assert.DoesNotContain(commands, sql => sql.Contains("WorldBeltRenewal", StringComparison.Ordinal));
                Assert.InRange(metrics.Connections, 1, 4);
                Assert.Equal(0, scheduler.Read().Failures);
                return;
            }
            fixture.Clock.Milliseconds += 50;
        }
        Assert.Fail("The remote hauler did not settle.");
    }

    [Fact]
    public void BackgroundAdmissionReservesTheLastActionCostAndStillRunsLargeUnits()
    {
        TestClock clock = new();
        CapsuleerBackgroundBudget budget = new(clock);
        using (budget.TryEnter("trade", CapsuleerAction.Trade))
        {
            clock.Milliseconds += 4;
        }
        Assert.Null(budget.TryEnter("trade", CapsuleerAction.Trade));
        Assert.Null(budget.TryEnter("unmeasured arrival", CapsuleerAction.Arrival));
        clock.Milliseconds += 50;
        using (var admitted = budget.TryEnter("trade", CapsuleerAction.Trade))
        {
            Assert.NotNull(admitted);
            clock.Milliseconds += 8;
        }
        clock.Milliseconds += 50;
        Assert.Null(budget.TryEnter("trade", CapsuleerAction.Trade));
        clock.Milliseconds += 50;
        using var retried = budget.TryEnter("trade", CapsuleerAction.Trade);
        Assert.NotNull(retried);
        Assert.Equal(1, budget.Overruns);
    }

    [Fact]
    public void BackgroundAdmissionAllowsExactRemainingAllowance()
    {
        TestClock clock = new();
        CapsuleerBackgroundBudget budget = new(clock);
        using (budget.TryEnter())
        {
            clock.Milliseconds += 4;
        }
        using (var last = budget.TryEnter())
        {
            Assert.NotNull(last);
            clock.Milliseconds += 1;
        }
        Assert.Null(budget.TryEnter());
        Assert.Equal(0, budget.Overruns);
    }

    [Fact]
    public void WarmRemoteSettlementsReuseRoleInputsWithoutReloadingFounders()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        for (int pass = 0; pass < 2; pass++)
        {
            fixture.Clock.Seconds += 70;
            List<string> commands = [];
            using var metrics = SessionPerformance.Begin("remote role inputs", _ => { }, force: true, command: commands.Add)!;
            for (int turn = 0; turn < 3; turn++)
            {
                scheduler.Run(checked((int)fixture.Clock.Seconds));
                fixture.Clock.Milliseconds += 50;
            }
            if (pass == 1)
            {
                Assert.DoesNotContain(commands, IsRoleLoad);
                Assert.InRange(metrics.Connections, 1, 20);
                Assert.InRange(metrics.Commands, 1, 180);
                Assert.InRange(metrics.FittingCalculations, 0, 4);
            }
        }
        Assert.Equal(0, scheduler.Read().Failures);
        Assert.True(scheduler.Read().CompletedActions >= 4);
    }

    private static bool IsRoleLoad(string sql) =>
        sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
        && (
            sql.Contains("FROM \"WorldCapsuleerCommunity\"", StringComparison.Ordinal)
            || sql.Contains("FROM \"WorldCapsuleerHauler\"", StringComparison.Ordinal)
        );

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedPilotChangesRefreshCompactRemoteRoles(bool returnToSystem)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        fixture.Clock.Seconds += 70;
        scheduler.Run(70);
        var live = host.GetRequiredService<LiveWorld>();
        long pilot = Assert.Single(live.MinerCheckpoints.Keys);
        for (int revision = 0; revision < 2; revision++)
        {
            string renamed = $"Updated remote pilot {revision}";
            using (WorldDbContext world = new(fixture.Database.Path) { Participants = TransactionParticipants.None })
            {
                world.Pilots.Find(pilot)!.Name = renamed;
                world.SaveChanges();
            }
            if (returnToSystem && revision == 1)
            {
                human.Transfer(Home);
            }
            Assert.Equal(renamed, scheduler.Miner(pilot)!.Pilot.Name);
        }
        Assert.Equal(0, scheduler.Read().Failures);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteShipInputInvalidationPublishesOnlyCommittedChanges(bool commit)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        fixture.Clock.Seconds += 70;
        scheduler.Run(70);
        long pilot = Assert.Single(host.GetRequiredService<LiveWorld>().MinerCheckpoints.Keys);
        var before = scheduler.Miner(pilot)!;
        const string renamed = "Updated remote ship";
        using (WorldDbContext world = new(fixture.Database.Path) { Participants = TransactionParticipants.None })
        {
            using var transaction = world.Database.BeginTransaction();
            world.Ships.Find(before.Pilot.ShipId)!.Name = renamed;
            world.SaveChanges();
            if (commit)
            {
                transaction.Commit();
            }
        }
        List<string> commands = [];
        using var metrics = SessionPerformance.Begin("remote ship inputs", _ => { }, force: true, command: commands.Add)!;
        var after = scheduler.Miner(pilot)!;
        Assert.Equal(commit ? renamed : before.Pilot.ShipName, after.Pilot.ShipName);
        if (!commit)
        {
            Assert.DoesNotContain(commands, IsRoleLoad);
            Assert.Equal(0, metrics.FittingCalculations);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FittingAndCatalogChangesRefreshCompactRemoteInputs(bool catalogChange)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        fixture.Clock.Seconds += 70;
        scheduler.Run(70);
        var live = host.GetRequiredService<LiveWorld>();
        long pilot = Assert.Single(live.MinerCheckpoints.Keys);
        var checkpoint = live.MinerCheckpoints[pilot];
        if (catalogChange)
        {
            // Disposing a writable catalog publishes a new generation, including
            // revisions whose changes do not affect this pilot's particular hull.
            using CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true);
        }
        else
        {
            using WorldDbContext world = new(fixture.Database.Path) { Participants = TransactionParticipants.None };
            // Skill and fitting triggers publish through this same dependency row.
            world.Database.ExecuteSql(
                $"""
                INSERT INTO WorldFitRevision(PilotId,Stamp) VALUES({pilot},'changed-inputs')
                ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp
                """
            );
        }
        List<string> commands = [];
        using var metrics = SessionPerformance.Begin("changed remote inputs", _ => { }, force: true, command: commands.Add)!;
        var refreshed = scheduler.Miner(pilot)!;
        Assert.Equal(pilot, refreshed.Pilot.PilotId);
        Assert.Equal(checkpoint.Work, refreshed.Work);
        Assert.Contains(commands, IsRoleLoad);
    }

    [Fact]
    public void CommittedPositionChangesRefreshCompactRemoteLocations()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        fixture.Clock.Seconds += 70;
        scheduler.Run(70);
        long pilot = Assert.Single(host.GetRequiredService<LiveWorld>().MinerCheckpoints.Keys);
        var before = scheduler.Miner(pilot)!;
        SpacePosition moved = new(1000, 2000, 3000);
        using (WorldDbContext world = new(fixture.Database.Path) { Participants = TransactionParticipants.None })
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath))
        {
            SqliteWorld.SetPosition(world, catalog, before.Pilot.ShipId, moved, Home, fixture.Clock.Now);
            world.SaveChanges();
        }
        Assert.Equal(moved, scheduler.Miner(pilot)!.Pilot.Position);
    }

    [Fact]
    public void CompactMinerReloadsGeometryWhenStartingItsReturn()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        MinerObservation? paid = null;
        for (int action = 0; action < 1000; action++)
        {
            paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
            if (paid.Work.Activity == MinerActivity.Mining)
            {
                break;
            }
        }
        Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
        long pilot = paid.Pilot.PilotId;
        host.GetRequiredService<LiveWorld>().MinerCheckpoints[pilot] = new(
            pilot,
            paid.Work with
            {
                Activity = MinerActivity.ReadyToReturn,
            }
        );
        var miner = host.GetRequiredService<CapsuleerMinerFactory>().Create(pilot);
        miner.Read(includeHealth: false);
        miner.SetDetailed(false);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var job = scheduler.Jobs().Single(j => j.PilotId == pilot);
        var activity = host.GetRequiredService<CapsuleerActivitySource>().Read();
        miner.Execute(job, checked((int)fixture.Clock.Seconds), activity.ElapsedTicks, activity.Resumes, background: true);
        var returning = miner.Read(includeHealth: false)!;
        Assert.Equal(MinerActivity.Returning, returning.Work.Activity);
        Assert.Equal(paid.Pilot.StationId, returning.Work.TargetId);
        Assert.Equal(0, returning.Work.PaidUntil);
    }

    [Theory]
    [InlineData(1, 12)]
    [InlineData(8, 4)]
    [InlineData(600, 4)]
    public void ResumeRetainsTheTransferWithoutReplayingAnExpiredArrival(int ageSeconds, int expectedArrivalUntil)
    {
        const long arrived = TimeSpan.TicksPerDay;
        HaulerProgress work = new(
            HaulerActivity.Flying,
            4,
            0,
            1,
            2,
            3,
            new NavigationState().Capture(),
            ArrivalUntil: 12,
            ArrivalGateId: 4,
            ArrivalAt: arrived
        );
        var resumed = CapsuleerPauseRules.Resume(
            work,
            TimeSpan.FromSeconds(ageSeconds).Ticks,
            arrived + TimeSpan.FromSeconds(ageSeconds).Ticks
        );
        Assert.Equal(expectedArrivalUntil, resumed.ArrivalUntil);
        Assert.Equal(work.ArrivalGateId, resumed.ArrivalGateId);
        Assert.Equal(work.ArrivalAt, resumed.ArrivalAt);
    }

    [Theory]
    [InlineData(120, 60000, 100, 10, 2)]
    [InlineData(120, 60000, 5, 10, 0)]
    [InlineData(59, 60000, 100, 10, 0)]
    [InlineData(600, 60000, 25, 10, 2)]
    [InlineData(120, 60000, 0, 0, 2)]
    public void AggregateCyclesRespectElapsedTimeAndAvailablePayment(int seconds, double duration, double charge, double cost, int expected)
    {
        MiningLaser laser = new(1, (int)MiningLaserType.MinerI, 1, duration, 1, cost, 0, 0);
        Assert.Equal(
            expected,
            CapsuleerCoarseRules.MiningCycles(new(TimeSpan.FromSeconds(seconds).Ticks, laser, charge, 10000, 10000, 1, false))
        );
    }

    [Theory]
    [InlineData(0, 1000, false, 0)]
    [InlineData(1, 1000, false, 1)]
    [InlineData(100, 3, false, 1)]
    [InlineData(1000, 1000, true, 1)]
    [InlineData(1000, 1000, false, 60)]
    public void AggregateCyclesStopAtCargoOreOrCrystalBoundaries(double hold, long ore, bool crystal, int expected)
    {
        MiningLaser laser = new(1, (int)MiningLaserType.MinerI, 1, 1000, 10, 0, 0, 0);
        Assert.Equal(expected, CapsuleerCoarseRules.MiningCycles(new(TimeSpan.FromMinutes(1).Ticks, laser, 0, hold, ore, 1, crystal)));
    }

    [Fact]
    public void BackgroundBudgetIsSharedAndDoesNotAccumulateUnusedWindows()
    {
        TestClock clock = new();
        CapsuleerBackgroundBudget budget = new(clock);
        using (budget.TryEnter())
        {
            Assert.Null(budget.TryEnter());
            clock.Milliseconds += 6;
        }
        Assert.Null(budget.TryEnter());
        Assert.Equal(1, budget.Overruns);
        clock.Milliseconds += 1000;
        using (budget.TryEnter())
        {
            clock.Milliseconds += 6;
        }
        Assert.Null(budget.TryEnter());
    }

    [Fact]
    public void LongAtomicSettlementBorrowsFutureAllowance()
    {
        TestClock clock = new();
        CapsuleerBackgroundBudget budget = new(clock);
        using (budget.TryEnter())
        {
            clock.Milliseconds += 20;
        }
        for (int window = 0; window < 3; window++)
        {
            clock.Milliseconds += 50;
            Assert.Null(budget.TryEnter());
        }
        clock.Milliseconds += 50;
        using var available = budget.TryEnter();
        Assert.NotNull(available);
    }

    [Fact]
    public void DistantJobsUseOneSystemCursorAndReturnDoesNotReplayHistory()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        Assert.Equal(0, scheduler.Read().ScheduledAgents);
        Assert.Equal(2, scheduler.Read().CoarseAgents);
        Assert.Equal(1, scheduler.Read().CoarseSystems);
        using (var metrics = SessionPerformance.Begin("cold remote jobs sleep", _ => { }, force: true)!)
        {
            for (int repeat = 0; repeat < 20; repeat++)
            {
                Assert.Equal(0, scheduler.Run(0).Turn.Completed);
            }
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        fixture.Clock.Seconds += 70;
        Assert.Equal(1, scheduler.Run(70).Turn.Completed);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            var cursor = Assert.Single(world.Set<SavedCapsuleerCoarseWork>());
            Assert.True(cursor.Cursor > 0);
            Assert.Equal(1, cursor.Generation);
            var job = scheduler.Jobs().Single(j => j.PilotId == cursor.Cursor);
            Assert.Equal(job.Revision, world.Set<SavedCapsuleerJob>().Find(job.PilotId)!.Revision);
        }
        fixture.Clock.Seconds += 3600;
        var before = scheduler.Jobs().ToDictionary(j => j.PilotId);
        human.Transfer(Home);
        scheduler.Run(3670, maximumActions: 1);
        Assert.All(scheduler.Jobs(), job => Assert.InRange(job.Sequence - before[job.PilotId].Sequence, 0, 1));
        Assert.True(scheduler.Read().SkippedRemoteTicks > 0);
        var returned = Assert.Single(scheduler.Read().RecentReturns);
        Assert.Equal(Home, returned.SystemId);
        Assert.Equal(1, returned.Bots);
        Assert.Equal(scheduler.Read().SkippedRemoteTicks, returned.MaximumSkippedTicks);
        Assert.InRange(returned.MaximumSkippedTicks, 1, TimeSpan.FromHours(1).Ticks);
    }

    [Fact]
    public async Task RestartPinsASavedMinerInteractionBeforeAnyRemoteSettlement()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        long pilot;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
            MinerObservation? paid = null;
            for (int action = 0; action < 1000; action++)
            {
                paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
                if (paid.Work.Activity == MinerActivity.Mining)
                {
                    break;
                }
            }
            Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
            pilot = paid.Pilot.PilotId;
            var live = host.GetRequiredService<LiveWorld>();
            live.MinerCheckpoints[pilot] = new(
                pilot,
                paid.Work with
                {
                    Defense = new() { ShipId = paid.Pilot.ShipId, RecallUntil = fixture.Clock.Now + TimeSpan.FromMinutes(1).Ticks },
                }
            );
            await live.FlushAsync();
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var selected = reopened.GetRequiredService<IHumanPresence>().Select(90000001, Elsewhere);
        var scheduler = reopened.GetRequiredService<CapsuleerScheduler>();
        scheduler.Run(checked((int)fixture.Clock.Seconds));
        Assert.True(scheduler.Read().ScheduledAgents > 0);
        Assert.Equal(1, scheduler.Read().CombatPinnedSystems);
        Assert.Equal(SystemSimulationTier.Detailed, scheduler.Systems().Single(s => s.SystemId == Home).Tier);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.DoesNotContain(saved.Set<SavedCapsuleerCoarseWork>(), w => w.Cursor == pilot);
    }

    [Fact]
    public void PreviousSchemaPreservesIdentitiesAndRestoresOnlyExplicitVisitorAdmission()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        var founder = fixture.Founder();
        HaulerProgress work = new(
            HaulerActivity.Docked,
            0,
            0,
            founder.FounderShipId,
            founder.DestinationStationId,
            0,
            new NavigationState().Capture(),
            TrafficLane: new(30000142, Home, 50009992, 50009991)
        );
        CapsuleerJob[] jobs;
        string progress;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            new HaulerCheckpoint(founder.PilotId, work).Apply(world);
            progress = world.Set<SavedCapsuleerHauler>().Single().Progress!;
            jobs = CapsuleerPopulationStore.Jobs(world).ToArray();
            world.Database.ExecuteSql($"INSERT OR IGNORE INTO WorldCapsuleerSimulation(SystemId,StartedTicks) VALUES({Home},0)");
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldCapsuleerCoarseWork;
                DROP TABLE WorldCapsuleerCoarsePilot;
                DROP TABLE WorldCapsuleerAdmission;
                PRAGMA user_version=137;
                """
            );
        }
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using WorldDbContext migrated = new(fixture.Database.Path);
        Assert.Equal(founder.PilotId, Assert.Single(migrated.Set<SavedCapsuleerAdmission>()).PilotId);
        Assert.Equal(Home, Assert.Single(migrated.Set<SavedCapsuleerSimulation>()).SystemId);
        Assert.Equal(jobs, CapsuleerPopulationStore.Jobs(migrated));
        Assert.Empty(migrated.Set<SavedCapsuleerCoarseWork>());
        Assert.Empty(migrated.Set<SavedCapsuleerCoarsePilot>());
        Assert.Equal(founder.FounderShipId, migrated.Pilots.Single(p => p.Id == founder.PilotId).ActiveShipId);
        Assert.Equal(founder.StartingCents, migrated.Set<Eden.Data.World.Market.PilotWallet>().Find(founder.PilotId)!.Cents);
        Assert.Equal(progress, migrated.Set<SavedCapsuleerHauler>().Single().Progress);
    }

    [Fact]
    public void PreparingAJourneyDoesNotActivateAnUnvisitedCommunity()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        var founder = fixture.Founder();
        HaulerProgress work = new(
            HaulerActivity.Docked,
            0,
            0,
            founder.FounderShipId,
            founder.DestinationStationId,
            0,
            new NavigationState().Capture()
        )
        {
            Journey = new(
                TrafficJourneyKind.Regional,
                founder.SourceStationId,
                founder.SourceStationId,
                founder.DestinationStationId,
                [Home, 30000142],
                1
            ),
        };
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            new HaulerCheckpoint(founder.PilotId, work).Apply(world);
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.PopulationPrepared(Home);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Elsewhere);
        scheduler.Run(0);
        Assert.Equal(0, scheduler.Read().CoarseAgents);
        Assert.Equal(0, scheduler.Read().ScheduledAgents);
        Assert.Equal(2, scheduler.Jobs().Count);
        human.Transfer(Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        Assert.Equal(2, scheduler.Read().CoarseAgents);
    }

    [Fact]
    public void EnteringSavedPopulationDoesNotHydrateEveryLegacyTraveler()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var metrics = SessionPerformance.Begin("cold membership uses compact metadata", _ => { }, force: true)!;
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        Assert.InRange(metrics.FittingCalculations, 0, 1);
    }

    [Fact]
    public void PopulationMetadataRefreshLeavesUnrelatedResidentMotionDirty()
    {
        using Combat.NpcCombatTests.Fixture fixture = new(liveWorld: true, useTestClock: true);
        var population = fixture.Db.Services.GetRequiredService<ICapsuleerPopulation>();
        population.Summary();
        var live = LiveWorld.Find(fixture.Db.Path)!;
        var runtime = live.CombatForSystem(Elsewhere)!;
        var original = fixture.Pilot.Position!;
        var moved = original + new SpacePosition(3000, 0, 0);
        runtime.PublishPilotPosition(Combat.NpcCombatTests.Fixture.ShipId, Elsewhere, moved);
        using (WorldDbContext world = new(fixture.Db.Path) { Participants = TransactionParticipants.None })
        {
            var saved = world.Set<SavedCapsuleerSystem>().Find(Home);
            if (saved is null)
            {
                world.Add(
                    new SavedCapsuleerSystem
                    {
                        SystemId = Home,
                        Eligibility = CapsuleerEligibility.NoMarket,
                        CatalogFingerprint = "test",
                        PolicyVersion = CapsuleerProvisioningRules.Version,
                    }
                );
            }
            else
            {
                saved.PolicyVersion++;
            }
            world.SaveChanges();
        }
        population.Summary();
        Assert.Equal(moved, runtime.InformationPosition(Combat.NpcCombatTests.Fixture.ShipId));
        using WorldDbContext committed = new(fixture.Db.Path) { Participants = TransactionParticipants.None };
        var position = committed.Positions.Single(p => p.ShipId == Combat.NpcCombatTests.Fixture.ShipId);
        Assert.Equal(original, new SpacePosition(position.X, position.Y, position.Z));
    }

    [Fact]
    public void UnloadedPopulationMetadataDoesNotReloadKnownPilots()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Add(
                new SavedCapsuleerSystem
                {
                    SystemId = Elsewhere,
                    Eligibility = CapsuleerEligibility.NoMarket,
                    CatalogFingerprint = "test",
                    PolicyVersion = CapsuleerProvisioningRules.Version,
                }
            );
            world.SaveChanges();
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        scheduler.Run(0);
        long loaded = scheduler.PopulationRowsLoaded;
        var jobs = scheduler.Jobs().ToArray();
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldCapsuleerSystem SET PolicyVersion=PolicyVersion+1 WHERE SystemId={Elsewhere}");
        }
        scheduler.Run(0);
        Assert.Equal(loaded, scheduler.PopulationRowsLoaded);
        Assert.Equal(jobs, scheduler.Jobs());
    }

    [Fact]
    public void ColdRemoteMinerReadsAsteroidsInItsOwnTransaction()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        MinerObservation? flying = null;
        for (int action = 0; action < 1000; action++)
        {
            flying = CapsuleerTestDriver.Miner(host, fixture.Clock);
            if (flying.Work.Activity == MinerActivity.FlyingToBelt)
            {
                break;
            }
        }
        Assert.Equal(MinerActivity.FlyingToBelt, flying!.Work.Activity);
        human.Transfer(Elsewhere);
        fixture.Clock.Seconds += 180;
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        for (int turn = 0; turn < CapsuleerCoarseRules.PageSize; turn++)
        {
            scheduler.Run(checked((int)fixture.Clock.Seconds));
            fixture.Clock.Milliseconds += 50;
        }
        Assert.Equal(0, scheduler.Read().Failures);
        var remote = host.GetRequiredService<LiveWorld>().MinerCheckpoints[flying.Pilot.PilotId].Work;
        Assert.Null(remote.WaitingReason);
        Assert.NotEqual(flying.Work.ActiveTicks, remote.ActiveTicks);
    }

    [Fact]
    public void LastObserverLeavingDemotesWithoutReplacingSavedPilots()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var presence = host.GetRequiredService<IHumanPresence>();
        using var first = presence.Select(90000001, Home);
        using var second = presence.Select(90000002, Home);
        var pilots = scheduler.Jobs().Select(j => j.PilotId).Order().ToArray();
        first.Transfer(Elsewhere);
        scheduler.Run(0);
        Assert.Equal(2, scheduler.Read().ScheduledAgents);
        Assert.Equal(0, scheduler.Read().CoarseAgents);
        second.Dispose();
        scheduler.Run(0);
        Assert.Equal(0, scheduler.Read().ScheduledAgents);
        Assert.Equal(2, scheduler.Read().CoarseAgents);
        Assert.Equal(pilots, scheduler.Jobs().Select(j => j.PilotId).Order());
    }

    [Fact]
    public void ReturningDuringAFuturePaidCyclePreservesItsRemainingTime()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSql(
                $"""
                UPDATE TypeAttribute SET Value=120000
                WHERE ItemTypeId={(int)MiningLaserType.MinerI} AND AttributeId={(int)DogmaAttributeId.Duration}
                """
            );
        }
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        MinerObservation? paid = null;
        for (int action = 0; action < 1000; action++)
        {
            paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
            if (paid.Work.Activity == MinerActivity.Mining)
            {
                break;
            }
        }
        Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
        human.Transfer(Elsewhere);
        long departed;
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            departed = world.Set<SavedCapsuleerCoarsePilot>().Find(paid.Pilot.PilotId)!.SettledTicks;
        }
        fixture.Clock.Seconds += 30;
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        long settled = 0;
        for (int turn = 0; turn < CapsuleerCoarseRules.PageSize; turn++)
        {
            scheduler.Run(checked((int)fixture.Clock.Seconds));
            using WorldDbContext world = new(fixture.Database.Path);
            settled = world.Set<SavedCapsuleerCoarsePilot>().Find(paid.Pilot.PilotId)?.SettledTicks ?? 0;
            if (settled > departed)
            {
                break;
            }
            fixture.Clock.Milliseconds += 50;
        }
        Assert.True(settled > departed);
        fixture.Clock.Seconds += 10;
        human.Transfer(Home);
        scheduler.Run(checked((int)fixture.Clock.Seconds), maximumActions: 1);
        var work = host.GetRequiredService<LiveWorld>().MinerCheckpoints[paid.Pilot.PilotId].Work;
        var activity = host.GetRequiredService<CapsuleerActivitySource>().Read();
        Assert.Equal(paid.Work.Cycles, work.Cycles);
        Assert.Equal(paid.Work.DueTicks + activity.ElapsedTicks - settled, work.DueTicks);
        Assert.Equal(fixture.Clock.Now + work.DueTicks - activity.ElapsedTicks, work.PaidUntil);
    }

    [Fact]
    public void CoarsePaidCollectionDepletesFiniteOreOnce()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        MinerObservation? paid = null;
        for (int action = 0; action < 1000; action++)
        {
            paid = CapsuleerTestDriver.Miner(host, fixture.Clock);
            if (paid.Work.Activity == MinerActivity.Mining)
            {
                break;
            }
        }
        Assert.Equal(MinerActivity.Mining, paid!.Work.Activity);
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity = 3;
            world.SaveChanges();
        }
        human.Transfer(Elsewhere);
        fixture.Clock.Seconds += 180;
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        var live = host.GetRequiredService<LiveWorld>();
        for (int turn = 0; turn < CapsuleerCoarseRules.PageSize; turn++)
        {
            scheduler.Run(checked((int)fixture.Clock.Seconds));
            fixture.Clock.Milliseconds += 50;
        }
        Assert.Equal(paid.Work.Cycles + 1, live.MinerCheckpoints[paid.Pilot.PilotId].Work.Cycles);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(0, saved.Set<WorldAsteroid>().Find(paid.Work.TargetId)!.Quantity);
        Assert.Equal(MinerActivity.FlyingToBelt, live.MinerCheckpoints[paid.Pilot.PilotId].Work.Activity);
    }

    [Fact]
    public void FailedCursorWriteRollsBackTheBotAndRetriesTheSamePilot()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        var before = scheduler.Jobs().ToArray();
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectCoarse BEFORE UPDATE ON WorldCapsuleerCoarseWork BEGIN SELECT RAISE(ABORT,'test'); END"
            );
        }
        fixture.Clock.Seconds += 70;
        Assert.Throws<SqliteException>(() => scheduler.Run(70));
        Assert.Equal(before, scheduler.Jobs());
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            Assert.Equal(0, Assert.Single(world.Set<SavedCapsuleerCoarseWork>()).Cursor);
            Assert.All(world.Set<SavedCapsuleerCoarsePilot>(), p => Assert.Equal(0, p.SettledTicks));
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectCoarse");
        }
        fixture.Clock.Milliseconds += 50;
        fixture.Clock.Seconds++;
        Assert.Equal(1, scheduler.Run(70).Turn.Completed);
        using WorldDbContext committed = new(fixture.Database.Path);
        Assert.Equal(1, Assert.Single(committed.Set<SavedCapsuleerCoarseWork>()).Generation);
        Assert.Single(committed.Set<SavedCapsuleerCoarsePilot>().Where(p => p.SettledTicks > 0));
    }

    [Fact]
    public void StaleCursorRefreshesWithoutPublishingSpeculativeWork()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
        human.Transfer(Elsewhere);
        scheduler.Run(0);
        var before = scheduler.Jobs().ToArray();
        using (WorldDbContext world = new(fixture.Database.Path))
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldCapsuleerCoarseWork SET Generation=Generation+1");
        }
        fixture.Clock.Seconds += 70;
        Assert.Equal(0, scheduler.Run(70).Turn.Completed);
        Assert.Equal(before, scheduler.Jobs());
        fixture.Clock.Milliseconds += 50;
        Assert.Equal(1, scheduler.Run(70).Turn.Completed);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(2, Assert.Single(saved.Set<SavedCapsuleerCoarseWork>()).Generation);
    }

    [Fact]
    public async Task CoarseCursorAndJobsReopenWithoutOfflineProduction()
    {
        using CapsuleerHaulerTests.Fixture fixture = new(isolateRoute: true);
        CapsuleerJob[] jobs;
        long generation;
        using (var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true))
        {
            var scheduler = host.GetRequiredService<CapsuleerScheduler>();
            using var human = host.GetRequiredService<IHumanPresence>().Select(90000001, Home);
            human.Transfer(Elsewhere);
            fixture.Clock.Seconds += 70;
            scheduler.Run(70);
            jobs = scheduler.Jobs().ToArray();
            using WorldDbContext world = new(fixture.Database.Path);
            generation = Assert.Single(world.Set<SavedCapsuleerCoarseWork>()).Generation;
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        fixture.Clock.Seconds += 86400;
        WorldInitialization.Initialize(fixture.Database, fixture.Clock);
        using var reopened = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<CapsuleerScheduler>();
        Assert.Equal(jobs, restored.Jobs());
        Assert.Equal(0, restored.Run(86470).Turn.Completed);
        using WorldDbContext saved = new(fixture.Database.Path);
        Assert.Equal(generation, Assert.Single(saved.Set<SavedCapsuleerCoarseWork>()).Generation);
    }
}
