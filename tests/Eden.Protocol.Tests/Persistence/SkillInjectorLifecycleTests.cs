using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillInjectorLifecycleTests
{
    [Fact]
    public void FixedValueInjectorsDoNotSpendAnUnneededDiminishingAllowance()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        BoosterTestData.Consume(db, SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor));
        var result = SkillInjectorTestData
            .Store(db)
            .Inject(SkillInjectorTestData.Pilot, SkillInjectorTestData.Item(db, SkillInjectorTestData.Fixed), 1);
        Assert.Equal(256_000, result.Points.Remaining);
        Assert.Equal(3, result.Status.AvailableUses);
    }

    [Fact]
    public void ConversionRejectsForeignItemsWrongTypesAndInsufficientSmallInjectorsWithoutChanges()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        long foreign = SkillInjectorTestData.Item(db);
        long daily = SkillInjectorTestData.Item(db, 46375);
        long small = SkillInjectorTestData.Item(db, 45635, 4);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(foreign)!.OwnerId = 90000002;
            world.SaveChanges();
        }
        var store = SkillInjectorTestData.Store(db);
        Assert.Throws<InvalidSkillInjection>(() => store.Convert(SkillInjectorTestData.Pilot, foreign, 1, true));
        Assert.Throws<InvalidSkillInjection>(() => store.Convert(SkillInjectorTestData.Pilot, daily, 1, true));
        Assert.Throws<InvalidSkillInjection>(() => store.Convert(SkillInjectorTestData.Pilot, small, 4, false));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(90000002, saved.Set<OwnedItem>().Find(foreign)!.OwnerId);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(daily)!.Quantity);
        Assert.Equal(4, saved.Set<OwnedItem>().Find(small)!.Quantity);
        Assert.Empty(saved.Set<SavedFreeSkillPoints>());
        Assert.Empty(saved.Set<SavedSkillInjection>());
    }

    [Fact]
    public void ExactMaximumBatchAndFailedConversionPreserveExpectedCapacity()
    {
        var plan = SkillInjectorRules.Plan(
            new(40520, SkillInjectorKind.Diminishing, 500_000, null),
            10_000,
            new(80_000_000, 0, AccountCloneGrade.Omega, 0, 0),
            0
        );
        Assert.Equal(1_500_000_000, plan.Points);
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        long small = SkillInjectorTestData.Item(db, 45635, int.MaxValue);
        long large = SkillInjectorTestData.Item(db);
        Assert.Throws<OverflowException>(() => SkillInjectorTestData.Store(db).Convert(SkillInjectorTestData.Pilot, large, 1, split: true));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(large)!.Quantity);
        Assert.Equal(int.MaxValue, saved.Set<OwnedItem>().Find(small)!.Quantity);
    }

    [Theory]
    [InlineData(40520, 500000)]
    [InlineData(43630, 25000)]
    [InlineData(45635, 100000)]
    [InlineData(46375, 50000)]
    [InlineData(49704, 256000)]
    public void EveryPublishedInjectorFamilyUsesItsPinnedProfile(int type, long points)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 0, grade: AccountCloneGrade.Alpha);
        var result = SkillInjectorTestData.Store(db).Inject(SkillInjectorTestData.Pilot, SkillInjectorTestData.Item(db, type), 1);
        Assert.Equal(points, result.Points.Remaining);
        Assert.Equal(0, Assert.Single(result.Changes).Item.Quantity);
    }

    [Theory]
    [InlineData(40520, 1650000)]
    [InlineData(45635, 330000)]
    public void AugmentorUsesAreSpentForEitherInjectorSizeAndHangoverKeepsTheSlotOccupied(int type, long points)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        long booster = SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor, 2);
        var active = BoosterTestData.Consume(db, booster);
        Assert.Equal(0, Assert.Single(active.Snapshot.Boosters).Bonus);
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(3, store.Read(SkillInjectorTestData.Pilot).AvailableUses);
        Assert.Equal(points, store.Preview(SkillInjectorTestData.Pilot, type, 4));
        Assert.Equal(3, store.Read(SkillInjectorTestData.Pilot).AvailableUses);
        var result = store.Inject(SkillInjectorTestData.Pilot, SkillInjectorTestData.Item(db, type, 4), 4);
        Assert.Equal(points, result.Points.Remaining);
        Assert.Equal(0, result.Status.AvailableUses);
        Assert.Single(BoosterTestData.Store(db).Read(SkillInjectorTestData.Pilot).Boosters);
        Assert.Throws<InvalidDataException>(() => BoosterTestData.Consume(db, booster));
        clock.AdvanceWallTime(TimeSpan.FromHours(16));
        Assert.Single(BoosterTestData.Consume(db, booster).Snapshot.Boosters);
        Assert.Equal(3, store.Read(SkillInjectorTestData.Pilot).AvailableUses);
    }

    [Fact]
    public void ExpirationRemovesUnusedAllowancesAndTheHistoricalInjectionDeadlineStillApplies()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        BoosterTestData.Consume(db, SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor));
        var store = SkillInjectorTestData.Store(db);
        clock.AdvanceWallTime(TimeSpan.FromHours(16));
        Assert.Equal(0, store.Read(SkillInjectorTestData.Pilot).AvailableUses);
        Assert.Equal(150_000, store.Preview(SkillInjectorTestData.Pilot, 40520, 1));
        clock.AdvanceWallTime(TimeSpan.FromDays(30));
        Assert.Throws<InvalidDataException>(() =>
            BoosterTestData.Consume(db, SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor))
        );
    }

    [Theory]
    [InlineData(6, 1, 1)]
    [InlineData(5, 0, 1)]
    [InlineData(10, 0, 2)]
    public void SmallLargeConversionConservesFullInjectorCapacityAndLeavesUncombinedRemainders(int count, int remainder, int output)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        long small = SkillInjectorTestData.Item(db, 45635, count);
        var store = SkillInjectorTestData.Store(db);
        var combined = store.Convert(SkillInjectorTestData.Pilot, small, count, split: false);
        Assert.Equal(remainder, combined[0].Item.Quantity);
        Assert.Equal(output, combined[1].Item.Quantity);
        Assert.Equal(40520, combined[1].Item.TypeId);
        var split = store.Convert(SkillInjectorTestData.Pilot, combined[1].Item.Id, output, split: true);
        Assert.Equal(0, split[0].Item.Quantity);
        Assert.Equal(count, split[1].Item.Quantity);
        Assert.Equal(45635, split[1].Item.TypeId);
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<SavedSkillInjection>());
        Assert.Empty(saved.Set<SavedFreeSkillPoints>());
        Assert.Equal(count, saved.Set<OwnedItem>().Where(i => i.TypeId == 45635).Sum(i => i.Quantity));
    }

    [Fact]
    public async Task ConcurrentConsumptionCreditsOneUnitAndForcedReceiptFailureRollsBackEverything()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000);
        BoosterTestData.Consume(db, SkillInjectorTestData.Item(db, SkillInjectorTestData.Augmentor));
        long item = SkillInjectorTestData.Item(db);
        var store = SkillInjectorTestData.Store(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectInjection BEFORE INSERT ON WorldSkillInjection BEGIN SELECT RAISE(ABORT,'test'); END;"
            );
        }
        Assert.Throws<DbUpdateException>(() => store.Inject(SkillInjectorTestData.Pilot, item, 1));
        Assert.Equal(3, store.Read(SkillInjectorTestData.Pilot).AvailableUses);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.NotNull(world.Set<OwnedItem>().Find(item));
            Assert.Equal(0, world.Set<SavedFreeSkillPoints>().Find(SkillInjectorTestData.Pilot)!.Points);
            Assert.Empty(world.Set<SavedSkillInjection>());
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectInjection");
        }
        var results = await Task.WhenAll(
            Enumerable.Range(0, 2).Select(_ => Task.Run(() => Record.Exception(() => store.Inject(SkillInjectorTestData.Pilot, item, 1))))
        );
        Assert.Single(results, e => e is null);
        Assert.IsType<InvalidSkillInjection>(Assert.Single(results, e => e is not null));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(500_000, saved.Set<SavedFreeSkillPoints>().Find(SkillInjectorTestData.Pilot)!.Points);
        Assert.Equal(2, store.Read(SkillInjectorTestData.Pilot).AvailableUses);
        Assert.Single(saved.Set<SavedSkillInjection>());
    }

    [Fact]
    public void CreditsAboveTheAlphaPassiveCapPauseTheRemainingQueueWithoutLosingElapsedProgress()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 4_999_750, grade: AccountCloneGrade.Alpha);
        var training = ImplantTestData.Training(db);
        training.Save(SkillInjectorTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(40));
        var result = SkillInjectorTestData.Store(db).Inject(SkillInjectorTestData.Pilot, SkillInjectorTestData.Item(db), 1);
        var entry = Assert.Single(result.Points.Training.Queue);
        Assert.Equal(10, entry.StartPoints);
        Assert.Null(entry.StartTime);
        Assert.Equal(500_000, result.Points.Remaining);
        Assert.DoesNotContain(training.History(SkillInjectorTestData.Pilot, 100), e => e.EventId == 53);
    }

    [Fact]
    public void BatchLimitsCapRejectionsAndOverflowDoNotConsumeStock()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        var store = SkillInjectorTestData.Store(db);
        long large = SkillInjectorTestData.Item(db, quantity: 10_001);
        Assert.Equal(
            "TooManyInjectorsInjected",
            Assert.Throws<InvalidSkillInjection>(() => store.Inject(SkillInjectorTestData.Pilot, large, 10_001)).Message
        );
        SkillInjectorTestData.Total(db, 1_460_000);
        long mini = SkillInjectorTestData.Item(db, 43630, 3);
        Assert.Throws<InvalidSkillInjection>(() => store.Inject(SkillInjectorTestData.Pilot, mini, 3));
        SkillInjectorTestData.Total(db, long.MaxValue - 1, free: long.MaxValue - 1);
        Assert.Throws<OverflowException>(() => store.Inject(SkillInjectorTestData.Pilot, large, 1));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(10_001, saved.Set<OwnedItem>().Find(large)!.Quantity);
        Assert.Equal(3, saved.Set<OwnedItem>().Find(mini)!.Quantity);
        Assert.Empty(saved.Set<SavedSkillInjection>());
    }

    [Fact]
    public void Version59MigrationPreservesExistingBoosterAndTrainingStateWithoutGrantingAllowances()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        var booster = Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db)).Snapshot.Boosters);
        var before = ImplantTestData.Training(db).Save(SkillInjectorTestData.Pilot, [new(3327, 1)], true);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldSkillInjection; DROP TABLE WorldSkillInjectorState; "
                    + "ALTER TABLE WorldBooster DROP COLUMN RemainingInjectionUses; PRAGMA user_version=59;"
            );
        }
        WorldInitialization.Open(db.Path);
        Assert.Equal(0, SkillInjectorTestData.Store(db).Read(SkillInjectorTestData.Pilot).AvailableUses);
        Assert.Equal(booster, Assert.Single(BoosterTestData.Store(db).Read(SkillInjectorTestData.Pilot).Boosters));
        Assert.Equal(before.Queue, ImplantTestData.Training(db).Advance(SkillInjectorTestData.Pilot).Queue);
    }
}
