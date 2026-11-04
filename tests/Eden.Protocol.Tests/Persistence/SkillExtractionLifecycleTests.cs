using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillExtractionLifecycleTests
{
    [Fact]
    public async Task ReceiptFailureRollsBackSkillsStockOutputAndHistoryThenConcurrentRequestsSpendOnlyOneItem()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        long item = SkillExtractionTestData.Item(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectExtraction BEFORE INSERT ON WorldSkillExtraction BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        var store = SkillExtractionTestData.Store(db);
        Assert.Throws<DbUpdateException>(() => store.Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request()));
        using (WorldDbContext world = new(db.Path))
        {
            Assert.Equal(8_000_000, world.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, SkillExtractionTestData.Filler)!.Points);
            Assert.NotNull(world.Set<OwnedItem>().Find(item));
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.TypeId == (int)SkillInjectorType.Large));
            Assert.Empty(world.Set<TrainingLog>());
            Assert.Empty(world.Set<SavedSkillExtraction>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectExtraction");
        }
        var results = await Task.WhenAll(
            Enumerable
                .Range(0, 2)
                .Select(_ =>
                    Task.Run(() =>
                        Record.Exception(() => store.Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request()))
                    )
                )
        );
        Assert.Single(results, r => r is null);
        Assert.IsType<InvalidSkillExtraction>(Assert.Single(results, r => r is not null));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(7_500_000, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, SkillExtractionTestData.Filler)!.Points);
        Assert.Equal(1, Assert.Single(saved.Set<OwnedItem>().Where(i => i.TypeId == (int)SkillInjectorType.Large)).Quantity);
        Assert.Single(saved.Set<SavedSkillExtraction>());
    }

    [Fact]
    public void OutputOverflowRollsBackRemovalAndExtractorConsumption()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        long item = SkillExtractionTestData.Item(db);
        long output = SkillInjectorTestData.Item(db, quantity: int.MaxValue);
        Assert.Throws<OverflowException>(() =>
            SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request())
        );
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Equal(int.MaxValue, saved.Set<OwnedItem>().Find(output)!.Quantity);
        Assert.Equal(8_000_000, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, SkillExtractionTestData.Filler)!.Points);
        Assert.Empty(saved.Set<TrainingLog>());
        Assert.Empty(saved.Set<SavedSkillExtraction>());
    }

    [Fact]
    public void ExtractionCheckpointsElapsedTrainingPreservesBoosterExpiryAndDoesNotTouchInjectorAllowancesOrPool()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Skill(db, BoosterTestData.Science, 8000);
        BoosterTestData.Consume(db, SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor));
        var booster = Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db)).Snapshot.Boosters, b => b.Bonus > 0);
        var training = ImplantTestData.Training(db);
        var before = Assert.Single(training.Save(SkillExtractionTestData.Pilot, [new(3327, 1)], true).Queue);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(40));
        var result = SkillExtractionTestData
            .Store(db)
            .Extract(SkillExtractionTestData.Pilot, SkillExtractionTestData.Item(db), SkillExtractionTestData.Request());
        var after = Assert.Single(result.Training.Queue);
        Assert.Equal(before, after);
        using (WorldDbContext checkpoint = new(db.Path))
        {
            Assert.Equal(before.PointsAt(clock.Now), checkpoint.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, 3327)!.Points);
        }
        Assert.Equal(before.EndTime, after.EndTime);
        Assert.Equal(booster, BoosterTestData.Store(db).Read(SkillExtractionTestData.Pilot).Boosters.Single(b => b.Bonus > 0));
        Assert.Equal(3, SkillInjectorTestData.Store(db).Read(SkillExtractionTestData.Pilot).AvailableUses);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(0, saved.Set<SavedFreeSkillPoints>().Find(SkillExtractionTestData.Pilot)!.Points);
        Assert.DoesNotContain(training.History(SkillExtractionTestData.Pilot, 100), e => e.EventId == 38);
    }

    [Fact]
    public void OfflineCompletionIsSettledBeforeMinimumAndQueueChecksAndVersion60MigrationPreservesState()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillInjectorTestData.Total(db, 5_499_750);
        var training = ImplantTestData.Training(db);
        var head = Assert.Single(training.Save(SkillExtractionTestData.Pilot, [new(3327, 1)], true).Queue);
        clock.AdvanceWallTime(TimeSpan.FromTicks(head.EndTime!.Value - clock.Now));
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldSkillExtraction; PRAGMA user_version=60");
        }
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        var result = SkillExtractionTestData
            .Store(db)
            .Extract(SkillExtractionTestData.Pilot, SkillExtractionTestData.Item(db), SkillExtractionTestData.Request());
        Assert.Empty(result.Training.Queue);
        Assert.Contains(3327, result.Training.ChangedSkills);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(250, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, 3327)!.Points);
        Assert.Equal(5_000_000, saved.Set<PilotSkill>().Where(s => s.PilotId == SkillExtractionTestData.Pilot).Sum(s => s.Points));
        Assert.Equal(WorldMigration.CurrentVersion, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
    }

    [Fact]
    public void ImplantPrerequisitesAreProtectedRecursivelyAndUnrelatedLegacyInvalidSkillsDoNotBlockExtraction()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Requires(db, ImplantTestData.Cybernetics, BoosterTestData.Science, 3);
        SkillExtractionTestData.Skill(db, ImplantTestData.Cybernetics, 750);
        SkillExtractionTestData.Skill(db, BoosterTestData.Science, 8000);
        ImplantTestData.Store(db).Install(SkillExtractionTestData.Pilot, ImplantTestData.Item(db));
        SkillExtractionTestData.Skill(db, ImplantTestData.Cybernetics, 0);
        var store = SkillExtractionTestData.Store(db);
        Assert.Throws<InvalidSkillExtraction>(() =>
            store.Extract(
                SkillExtractionTestData.Pilot,
                SkillExtractionTestData.Item(db),
                [new(BoosterTestData.Science, 1), new(SkillExtractionTestData.Filler, 499999)]
            )
        );
        Assert.NotNull(store.Extract(SkillExtractionTestData.Pilot, SkillExtractionTestData.Item(db), SkillExtractionTestData.Request()));
    }

    [Fact]
    public void ElapsedPartialTrainingCountsTowardsTheAllocatedMinimumAndAlphaCanExtractAnOmegaSkill()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillInjectorTestData.Total(db, 5_499_999);
        var head = Assert.Single(ImplantTestData.Training(db).Save(SkillExtractionTestData.Pilot, [new(3327, 1)], true).Queue);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(2));
        var result = SkillExtractionTestData
            .Store(db)
            .Extract(SkillExtractionTestData.Pilot, SkillExtractionTestData.Item(db), SkillExtractionTestData.Request());
        Assert.Equal(head, Assert.Single(result.Training.Queue));
        using (WorldDbContext checkpoint = new(db.Path))
        {
            Assert.Equal(1, checkpoint.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, 3327)!.Points);
        }
        Assert.Equal(head.EndTime, Assert.Single(result.Training.Queue).EndTime);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Alpha;
            world.SaveChanges();
        }
        ImplantTestData.Training(db).Pause(SkillExtractionTestData.Pilot);
        SkillExtractionTestData.Skill(db, SkillExtractionTestData.Filler, 5_500_000);
        Assert.NotNull(
            SkillExtractionTestData
                .Store(db)
                .Extract(SkillExtractionTestData.Pilot, SkillExtractionTestData.Item(db), SkillExtractionTestData.Request())
        );
    }

    [Fact]
    public void RepeatedExtractionsPreserveFractionalProgressAndTheOriginalBoosterExpirationSchedule()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Skill(db, BoosterTestData.Science, 8000);
        var booster = Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db)).Snapshot.Boosters);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<SavedBooster>().Find(booster.ItemId)!.ExpiresAt = clock.Now + TimeSpan.FromSeconds(2).Ticks;
            world.SaveChanges();
        }
        var training = ImplantTestData.Training(db);
        var before = Assert.Single(training.Save(SkillExtractionTestData.Pilot, [new(3327, 1)], true).Queue);
        Assert.Equal(2, before.Periods.Count);
        long item = SkillExtractionTestData.Item(db, 4);
        for (int i = 0; i < 4; i++)
        {
            clock.AdvanceWallTime(TimeSpan.FromMilliseconds(750));
            var result = SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request());
            Assert.Equal(before, Assert.Single(result.Training.Queue));
            using WorldDbContext checkpoint = new(db.Path);
            Assert.Equal(before.PointsAt(clock.Now), checkpoint.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, 3327)!.Points);
        }
        clock.AdvanceWallTime(TimeSpan.FromTicks(before.EndTime!.Value - clock.Now));
        Assert.Empty(training.Advance(SkillExtractionTestData.Pilot).Queue);
        Assert.Single(training.History(SkillExtractionTestData.Pilot, 100), e => e.EventId == 53);
        Assert.DoesNotContain(training.History(SkillExtractionTestData.Pilot, 100), e => e.EventId == 38);
    }
}
