using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

internal static class FreePointTestData
{
    internal const long Pilot = RedemptionTests.Pilot;

    internal static TestWorld Create(TestClock clock)
    {
        var db = RedemptionTests.Create(clock);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            "INSERT OR IGNORE INTO ItemCategory VALUES (63,'Special Edition Assets'); "
                + "INSERT OR IGNORE INTO ItemGroup VALUES (999900,'SP rewards',63);"
        );
        foreach (int type in SkillPointRewards.Amounts.Keys)
        {
            catalog.Database.ExecuteSql($"INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES ({type},'SP reward',999900)");
        }
        using WorldDbContext world = new(db.Path);
        world.Set<PilotSkill>().Find(Pilot, 3327)!.Points = 0;
        world.SaveChanges();
        return db;
    }

    internal static RedeemResult Credit(TestWorld db, int type = 54652, int quantity = 1)
    {
        var store = RedemptionTests.Store(db);
        var token = store.Grant(RedemptionTests.Grant(Guid.NewGuid().ToString("N"), type, quantity));
        return store.Claim(RedemptionTests.Account, Pilot, [new(token.Id, type)], false);
    }

    internal static IPilotFreeSkillPoints Points(TestWorld db) => db.Services.GetRequiredService<IPilotFreeSkillPoints>();

    internal static IPilotTraining Training(TestWorld db) => db.Services.GetRequiredService<IPilotTraining>();
}

public sealed class FreeSkillPointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingApplicationsCannotSpendTheSamePointsTwice(bool resident)
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        using LiveWorld? live = resident ? new(new(db.Path), clock, _ => { }) : null;
        var attempts = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                    {
                        try
                        {
                            return FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Single(3327, 750));
                        }
                        catch (InvalidTrainingQueue)
                        {
                            return null;
                        }
                    })
                )
        );
        Assert.Single(attempts, a => a is not null);
        Assert.Equal(250, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Equal(750, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(FreePointTestData.Pilot)).Points);
        Assert.Single(FreePointTestData.Training(db).History(FreePointTestData.Pilot, 100), h => h.EventId == 307);
    }

    [Fact]
    public void AFailedTokenCommitRollsBackCreditTrainingProgressQueueHistoryAndRevision()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var store = RedemptionTests.Store(db);
        var token = store.Grant(RedemptionTests.Grant("failure", 54652, 1));
        var training = FreePointTestData.Training(db);
        var before = training.Save(FreePointTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_credit BEFORE UPDATE ON WorldRedeemToken " + "BEGIN SELECT RAISE(ABORT,'token failed'); END";
        command.ExecuteNonQuery();
        Assert.Throws<DbUpdateException>(() =>
            store.Claim(RedemptionTests.Account, FreePointTestData.Pilot, [new(token.Id, 54652)], false)
        );
        Assert.Equal(new FreeSkillPointBalance(0, 0), FreePointTestData.Points(db).Observe(FreePointTestData.Pilot));
        Assert.Equal(token, Assert.Single(store.Read(RedemptionTests.Account)));
        Assert.Equal(before.Queue, training.Advance(FreePointTestData.Pilot).Queue);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(0, world.Set<PilotSkill>().Find(FreePointTestData.Pilot, 3327)!.Points);
        Assert.Single(world.Set<TrainingLog>());
    }

    [Fact]
    public void CreditsRebaseAContinuingSkillWithoutInventingAnotherTrainingStart()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var training = FreePointTestData.Training(db);
        var before = training.Save(FreePointTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        var credit = FreePointTestData.Credit(db);
        Assert.Equal(before.Queue[0].EndTime, Assert.Single(credit.Training!.Queue).EndTime);
        Assert.Single(training.History(FreePointTestData.Pilot, 100));
        Assert.Equal(1, TaskTrackingTestData.Count(db, Eden.Gameplay.Characters.TaskEvents.TrainingStarted));
    }

    [Fact]
    public void ARealVersion55MigrationRetainsTheQueueAndRedeemTokensAndStartsWithNoFreePoints()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var token = RedemptionTests.Store(db).Grant(RedemptionTests.Grant("pending-sp", 54652, 1));
        var saved = FreePointTestData.Training(db).Save(FreePointTestData.Pilot, [new(3327, 1)], false);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldFreeSkillPoints; PRAGMA user_version=55;");
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(saved.Queue, FreePointTestData.Training(db).Advance(FreePointTestData.Pilot).Queue);
        Assert.Equal(0, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Equal(token, Assert.Single(RedemptionTests.Store(db).Read(RedemptionTests.Account)));
        Assert.Equal(1000, FreePointTestData.Credit(db).SkillPoints);
    }

    [Fact]
    public void OrderedTargetsHonorPrerequisitesWhileOmegaAndSingleLevelFiveClippingUseTheActualBudget()
    {
        TrainingInputs inputs = new(
            new Dictionary<int, TrainingSkill> { [1] = new(0, 1, 20, 20, 5, []), [2] = new(0, 1, 20, 20, 5, [new(1, 1)]) }
        );
        Assert.Throws<InvalidTrainingQueue>(() => FreeSkillPointRules.Plan(1000, new FreePointRequest.Targets([new(2, 1)]), inputs, []));
        var planned = FreeSkillPointRules.Plan(1000, new FreePointRequest.Targets([new(1, 1), new(2, 1)]), inputs, []);
        Assert.Equal(500, planned.Remaining);
        Assert.Equal(new int[] { 1, 2 }, planned.Allocations.Select(a => a.TypeId));
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db, 52520);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(RedemptionTests.Account)!.CloneGrade = AccountCloneGrade.Omega;
            world.Set<PilotSkill>().Find(FreePointTestData.Pilot, 3327)!.Points = 250000;
            world.SaveChanges();
        }
        var applied = FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Single(3327, 10000));
        Assert.Equal(4000, applied.Remaining);
        Assert.Equal(256000, Assert.Single(applied.Allocations).After);
        Assert.Empty(FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Single(3327, 1)).Allocations);
    }

    [Fact]
    public void RedeemedPointsSurviveReopenAndSingleApplicationConsumesExactlyTheCommittedAmount()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var result = FreePointTestData.Credit(db);
        Assert.Empty(result.Items);
        Assert.Empty(result.Errors);
        Assert.Equal(1000, result.SkillPoints);
        Assert.Equal(1000, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Equal(0, FreePointTestData.Points(db).Balance(90000002));
        var applied = FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Single(3327, 250));
        Assert.Equal(750, applied.Remaining);
        Assert.Equal(new SkillPointAllocation(3327, 0, 250), Assert.Single(applied.Allocations));
        Assert.Equal(307, Assert.Single(FreePointTestData.Training(db).History(FreePointTestData.Pilot, 100)).EventId);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        Assert.Equal(750, reopened.GetRequiredService<IPilotFreeSkillPoints>().Balance(FreePointTestData.Pilot));
        Assert.Equal(250, Assert.Single(reopened.GetRequiredService<IPilotSkillReader>().ReadSkills(FreePointTestData.Pilot)).Points);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreviewDoesNotCommitOfflineCompletionsAndQueueApplicationPreservesElapsedTrainingOnce(bool resident)
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        var training = FreePointTestData.Training(db);
        training.Save(FreePointTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        using LiveWorld? live = resident ? new(new(db.Path), clock, _ => { }) : null;
        clock.AdvanceWallTime(TimeSpan.FromSeconds(2000));
        var points = FreePointTestData.Points(db);
        var preview = points.Preview(FreePointTestData.Pilot, new FreePointRequest.Queue());
        Assert.Equal(new SkillPointAllocation(3327, 500, 1415), Assert.Single(preview.Allocations));
        Assert.Equal(85, preview.Remaining);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(0, world.Set<PilotSkill>().Find(FreePointTestData.Pilot, 3327)!.Points);
            Assert.Equal(2, world.Set<QueuedLevel>().Count());
            Assert.DoesNotContain(world.Set<TrainingLog>(), h => h.EventId is 53 or 307);
        }
        var applied = points.Apply(FreePointTestData.Pilot, new FreePointRequest.Queue());
        Assert.Equal(preview.Remaining, applied.Remaining);
        Assert.Equal(preview.Allocations, applied.Allocations);
        Assert.Empty(applied.Training.Queue);
        Assert.Empty(points.Apply(FreePointTestData.Pilot, new FreePointRequest.Queue()).Allocations);
        Assert.Equal(85, points.Balance(FreePointTestData.Pilot));
        Assert.Single(training.History(FreePointTestData.Pilot, 100), h => h.EventId == 53);
        Assert.Single(training.History(FreePointTestData.Pilot, 100), h => h.EventId == 307);
    }

    [Fact]
    public void ApplyingPartOfTheQueueRebasesItsDeadlineWithoutLosingElapsedPointsOrRepeatingCompletion()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        var training = FreePointTestData.Training(db);
        training.Save(FreePointTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        var state = FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Queue());
        Assert.Equal(new SkillPointAllocation(3327, 30, 1030), Assert.Single(state.Allocations));
        Assert.Equal(0, state.Remaining);
        var head = Assert.Single(state.Training.Queue);
        Assert.Equal(2, head.Target.Level);
        Assert.Equal(1030, head.StartPoints);
        Assert.Equal(clock.UtcNow.AddSeconds(1540).UtcDateTime.ToFileTimeUtc(), head.EndTime);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(1540));
        Assert.Empty(training.Advance(FreePointTestData.Pilot).Queue);
        Assert.Empty(training.Advance(FreePointTestData.Pilot).ChangedSkills);
        Assert.Equal(1415, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(FreePointTestData.Pilot)).Points);
        Assert.Single(training.History(FreePointTestData.Pilot, 100), h => h.EventId == 53);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectCurrentTrainingAndOverspendingRejectWithThePoolQueueAndHistoryUnchanged(bool resident)
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        var training = FreePointTestData.Training(db);
        var before = training.Save(FreePointTestData.Pilot, [new(3327, 1)], true);
        using LiveWorld? live = resident ? new(new(db.Path), clock, _ => { }) : null;
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        var points = FreePointTestData.Points(db);
        Assert.Throws<InvalidTrainingQueue>(() => points.Apply(FreePointTestData.Pilot, new FreePointRequest.Single(3327, 100)));
        Assert.Equal(before.Queue, training.Advance(FreePointTestData.Pilot).Queue);
        training.Pause(FreePointTestData.Pilot);
        foreach (
            var request in new FreePointRequest[]
            {
                new FreePointRequest.Single(3327, 1001),
                new FreePointRequest.Single(3327, -1),
                new FreePointRequest.Single(999999, 1),
                new FreePointRequest.Targets([new(3327, 1), new(3327, 1)]),
            }
        )
        {
            Assert.Throws<InvalidTrainingQueue>(() => points.Apply(FreePointTestData.Pilot, request));
        }
        Assert.Equal(1000, points.Balance(FreePointTestData.Pilot));
        Assert.DoesNotContain(training.History(FreePointTestData.Pilot, 100), h => h.EventId == 307);
    }

    [Fact]
    public void AlphaPoolCountsTowardPassiveCapButCanBeSpentBeyondFiveMillionWithinAlphaSkillLevels()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db, 52263, 6);
        var training = FreePointTestData.Training(db);
        Assert.Throws<InvalidTrainingQueue>(() => training.Save(FreePointTestData.Pilot, [new(3327, 1)], true));
        var points = FreePointTestData.Points(db);
        var applied = points.Apply(FreePointTestData.Pilot, new FreePointRequest.Targets([new(3327, 1)]));
        Assert.Equal(5_999_750, applied.Remaining);
        Assert.Equal(250, Assert.Single(applied.Allocations).After);
        Assert.Throws<InvalidTrainingQueue>(() => points.Apply(FreePointTestData.Pilot, new FreePointRequest.Targets([new(3327, 5)])));
        Assert.Equal(5_999_750, points.Balance(FreePointTestData.Pilot));
    }

    [Fact]
    public void RewardNearTheAlphaCapKeepsAvailableTrainingThenPausesTheUnfinishedLevelAtTheExactCap()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        var training = FreePointTestData.Training(db);
        training.Save(FreePointTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        using (WorldDbContext world = new(db.Path))
        {
            using var transaction = world.Database.BeginTransaction();
            new PilotTrainingStore(new(db.Path), clock).Credit(world, FreePointTestData.Pilot, 4_999_000, clock.Now);
            transaction.Commit();
        }
        var queue = training.Advance(FreePointTestData.Pilot).Queue;
        Assert.Equal(250, queue[0].EndPoints);
        Assert.Equal(1000, queue[1].EndPoints);
        Assert.Equal(queue, training.Save(FreePointTestData.Pilot, queue.Select(q => q.Target).ToArray(), true).Queue);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(4000));
        var stopped = training.Advance(FreePointTestData.Pilot);
        var remaining = Assert.Single(stopped.Queue);
        Assert.Equal(new TrainingTarget(3327, 2), remaining.Target);
        Assert.Equal(1000, remaining.StartPoints);
        Assert.Equal(1415, remaining.EndPoints);
        Assert.Null(remaining.StartTime);
        Assert.Equal(1000, Assert.Single(db.Services.GetRequiredService<IPilotSkillReader>().ReadSkills(FreePointTestData.Pilot)).Points);
        Assert.Single(training.History(FreePointTestData.Pilot, 100), h => h.EventId == 53);
        Assert.Empty(training.Advance(FreePointTestData.Pilot).ChangedSkills);
    }

    [Fact]
    public void FailedApplicationHistoryWriteRollsBackSpentPointsElapsedTrainingAndQueueChanges()
    {
        var clock = CalendarTestData.Clock();
        using var db = FreePointTestData.Create(clock);
        FreePointTestData.Credit(db);
        var training = FreePointTestData.Training(db);
        var before = training.Save(FreePointTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(120));
        using var connection = new SqliteConnection($"Data Source={db.Path};Pooling=false");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TRIGGER fail_sp BEFORE INSERT ON WorldSkillHistory WHEN NEW.EventId=307 "
            + "BEGIN SELECT RAISE(ABORT,'application failed'); END";
        command.ExecuteNonQuery();
        Assert.Throws<DbUpdateException>(() => FreePointTestData.Points(db).Apply(FreePointTestData.Pilot, new FreePointRequest.Queue()));
        Assert.Equal(1000, FreePointTestData.Points(db).Balance(FreePointTestData.Pilot));
        Assert.Equal(before.Queue, training.Advance(FreePointTestData.Pilot).Queue);
        using WorldDbContext world = new(db.Path);
        Assert.Equal(0, world.Set<PilotSkill>().Find(FreePointTestData.Pilot, 3327)!.Points);
        Assert.DoesNotContain(world.Set<TrainingLog>(), h => h.EventId == 307);
    }
}
