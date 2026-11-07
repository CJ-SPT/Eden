using Eden.Data.Inventory;
using Eden.Data.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillInjectorTests
{
    [Theory]
    [InlineData(0, 500000, 100000)]
    [InlineData(4999999, 500000, 100000)]
    [InlineData(5000000, 400000, 80000)]
    [InlineData(49999999, 400000, 80000)]
    [InlineData(50000000, 300000, 60000)]
    [InlineData(79999999, 300000, 60000)]
    [InlineData(80000000, 150000, 30000)]
    public void TierBoundariesUseAllocatedAndUnallocatedPoints(long total, long large, long small)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, total, free: total / 2);
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(large, store.Preview(SkillInjectorTestData.Pilot, (int)SkillInjectorType.Large, 1));
        Assert.Equal(small, store.Preview(SkillInjectorTestData.Pilot, (int)SkillInjectorType.Small, 1));
        Assert.Equal(total - total / 2, store.AllocatedPoints(SkillInjectorTestData.Pilot));
    }

    [Fact]
    public void BatchInjectionCrossesTiersPerUnitAndPreviewsLeaveEverySavedValueUnchanged()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 4_501_000, free: 1_000_000);
        long item = SkillInjectorTestData.Item(db, quantity: 3);
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(900_000, store.Preview(SkillInjectorTestData.Pilot, (int)SkillInjectorType.Large, 2));
        store.Check(SkillInjectorTestData.Pilot, item, 2);
        using (WorldDbContext before = new(db.Path))
        {
            Assert.Equal(1_000_000, before.Set<SavedFreeSkillPoints>().Find(SkillInjectorTestData.Pilot)!.Points);
            Assert.Equal(3, before.Set<OwnedItem>().Find(item)!.Quantity);
            Assert.Empty(before.Set<SavedSkillInjection>());
            Assert.Empty(before.Set<SavedSkillInjectorState>());
        }
        var result = store.Inject(SkillInjectorTestData.Pilot, item, 2);
        Assert.Equal(1_900_000, result.Points.Remaining);
        Assert.Equal(1, Assert.Single(result.Changes).Item.Quantity);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        Assert.Equal(1_900_000, reopened.GetRequiredService<IPilotFreeSkillPoints>().Balance(SkillInjectorTestData.Pilot));
        using WorldDbContext saved = new(db.Path);
        var receipt = Assert.Single(saved.Set<SavedSkillInjection>());
        Assert.Equal(4_501_000, receipt.TotalBefore);
        Assert.Equal(5_401_000, receipt.TotalAfter);
        Assert.Equal(900_000, receipt.Points);
    }

    [Theory]
    [InlineData(1490000, 1, 10000)]
    [InlineData(1475000, 1, 25000)]
    [InlineData(1460000, 2, 40000)]
    public void MiniInjectorsRespectTheirCatalogTotalLimit(long total, int count, long awarded)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, total);
        long item = SkillInjectorTestData.Item(db, (int)SkillInjectorType.Mini, count + 1);
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(awarded, store.Preview(SkillInjectorTestData.Pilot, (int)SkillInjectorType.Mini, count));
        Assert.Equal(awarded, store.Inject(SkillInjectorTestData.Pilot, item, count).Points.Remaining);
        var error = Assert.Throws<InvalidSkillInjection>(() => store.Inject(SkillInjectorTestData.Pilot, item, 1));
        Assert.Equal("InjectorSkillPointLimitReached", error.Message);
        Assert.Equal(1_500_000, error.Limit);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(item)!.Quantity);
    }

    [Fact]
    public void DailyAlphaCooldownIsPerPilotPersistsAndAllowsTheExact24HourBoundary()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 80_000_000, grade: AccountCloneGrade.Alpha);
        long item = SkillInjectorTestData.Item(db, (int)SkillInjectorType.DailyAlpha, 3);
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(50_000, store.Inject(SkillInjectorTestData.Pilot, item, 1).Points.Remaining);
        long deadline = clock.Now + TimeSpan.TicksPerDay;
        Assert.Equal(deadline, store.Read(SkillInjectorTestData.Pilot).NextAlphaInjection);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var restored = reopened.GetRequiredService<IPilotSkillInjectors>();
        Assert.Equal(
            "AlreadyInjectedToday",
            Assert.Throws<InvalidSkillInjection>(() => restored.Check(SkillInjectorTestData.Pilot, item, 1)).Message
        );
        Assert.Equal(0, restored.Read(90000002).NextAlphaInjection);
        clock.AdvanceWallTime(TimeSpan.FromHours(24) - TimeSpan.FromTicks(1));
        Assert.Throws<InvalidSkillInjection>(() => restored.Inject(SkillInjectorTestData.Pilot, item, 1));
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(100_000, restored.Inject(SkillInjectorTestData.Pilot, item, 1).Points.Remaining);
    }

    [Fact]
    public void CurrentTrainingProgressParticipatesInTheTierAndPreviewDoesNotMaterializeIt()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        SkillInjectorTestData.Total(db, 4_999_990);
        var training = ImplantTestData.Training(db);
        var queue = training.Save(SkillInjectorTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(20));
        long item = SkillInjectorTestData.Item(db);
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(400_000, store.Preview(SkillInjectorTestData.Pilot, (int)SkillInjectorType.Large, 1));
        Assert.Equal(5_000_000, store.AllocatedPoints(SkillInjectorTestData.Pilot));
        Assert.Equal(queue.Queue, training.Advance(SkillInjectorTestData.Pilot).Queue);
        using (WorldDbContext before = new(db.Path))
        {
            Assert.Equal(0, before.Set<PilotSkill>().Find(SkillInjectorTestData.Pilot, 3327)!.Points);
        }
        var result = store.Inject(SkillInjectorTestData.Pilot, item, 1);
        Assert.Equal(400_000, result.Points.Remaining);
        Assert.Equal(10, Assert.Single(result.Points.Training.Queue).StartPoints);
        Assert.Equal(queue.Queue[0].EndTime, result.Points.Training.Queue[0].EndTime);
        Assert.Single(training.History(SkillInjectorTestData.Pilot, 100));
    }

    [Theory]
    [InlineData("foreign", "YouDoNotOwnCharacterServiceItem")]
    [InlineData("remote", "CharacterServiceItemNotInStation")]
    [InlineData("cargo", "CharacterServiceItemNotInStation")]
    [InlineData("space", "CharacterServiceItemNotInStation")]
    [InlineData("quantity", "InconsistentInjectorQuantity")]
    [InlineData("unpublished", "IncorrectServiceItemType")]
    [InlineData("omega", "RestrictedToAlphaClones")]
    public void RejectedRequestsLeaveItemsPointsAndReceiptsUnchanged(string reason, string expected)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillInjectorTestData.Create(clock);
        long item = SkillInjectorTestData.Item(
            db,
            reason == "omega" ? (int)SkillInjectorType.DailyAlpha
                : reason == "unpublished" ? 42523
                : 40520,
            2
        );
        using (WorldDbContext world = new(db.Path))
        {
            var stack = world.Set<OwnedItem>().Find(item)!;
            if (reason == "foreign")
            {
                stack.OwnerId = 90000002;
            }
            if (reason == "remote")
            {
                stack.LocationId = HomeStationTestData.Other;
            }
            if (reason == "cargo")
            {
                stack.LocationId = db.Store.FindPilot(1000001)!.ShipId;
                stack.FlagId = (int)InventoryFlag.Cargo;
            }
            world.SaveChanges();
        }
        if (reason == "space")
        {
            db.Store.SetPosition(db.Store.FindPilot(1000001)!.ShipId, SpacePosition.Zero);
        }
        var store = SkillInjectorTestData.Store(db);
        Assert.Equal(
            expected,
            Assert
                .Throws<InvalidSkillInjection>(() => store.Inject(SkillInjectorTestData.Pilot, item, reason == "quantity" ? 3 : 1))
                .Message
        );
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Empty(saved.Set<SavedSkillInjection>());
        Assert.Empty(saved.Set<SavedSkillInjectorState>());
        Assert.Empty(saved.Set<SavedFreeSkillPoints>());
    }
}
