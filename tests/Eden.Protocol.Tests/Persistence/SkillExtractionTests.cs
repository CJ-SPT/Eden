using System.Text.Json;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Skills;
using Eden.World.Characters;
using Eden.World.Docking;
using Eden.World.Inventory;
using Eden.World.Skills;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillExtractionTests
{
    [Fact]
    public void ExactMinimumConsumesOneExtractorMergesOutputAndPersistsReceiptAndSkillHistory()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillInjectorTestData.Total(db, 6_500_000, free: 1_000_000);
        long item = SkillExtractionTestData.Item(db, 2);
        long output = SkillInjectorTestData.Item(db, quantity: 3);
        var result = SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request());
        Assert.Equal(1, result.Changes[0].Item.Quantity);
        Assert.Equal(output, result.Changes[1].Item.Id);
        Assert.Equal(4, result.Changes[1].Item.Quantity);
        Assert.Contains(SkillExtractionTestData.Filler, result.Training.ChangedSkills);
        WorldInitialization.Initialize(new WorldDatabase(db.Path), clock);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(5_000_000, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, SkillExtractionTestData.Filler)!.Points);
        Assert.Equal(1_000_000, saved.Set<SavedFreeSkillPoints>().Find(SkillExtractionTestData.Pilot)!.Points);
        var audit = Assert.Single(saved.Set<SavedSkillExtraction>());
        Assert.Equal(item, audit.ItemId);
        Assert.Equal(clock.Now, audit.Time);
        var allocation = Assert.Single(JsonSerializer.Deserialize<SkillPointAllocation[]>(audit.AllocationsJson)!);
        Assert.Equal(500_000, -allocation.Spent);
        Assert.Equal(result.Revision, SkillExtractionTestData.Store(db).Revision(SkillExtractionTestData.Pilot));
        var history = Assert.Single(ImplantTestData.Training(db).History(SkillExtractionTestData.Pilot, 100));
        Assert.Equal(SkillExtractionRules.HistoryEvent, history.EventId);
        Assert.Equal(allocation.After, history.Points);
    }

    [Fact]
    public void UnallocatedPointsDoNotCountTowardsTheMinimumAndFailedExtractionChangesNothing()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillInjectorTestData.Total(db, 9_499_999, free: 4_000_000);
        long item = SkillExtractionTestData.Item(db);
        Assert.Equal(
            "SkillExtractionNotEnoughSP",
            Assert
                .Throws<InvalidSkillExtraction>(() =>
                    SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request())
                )
                .Message
        );
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(5_499_999, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, SkillExtractionTestData.Filler)!.Points);
        Assert.Equal(4_000_000, saved.Set<SavedFreeSkillPoints>().Find(SkillExtractionTestData.Pilot)!.Points);
        Assert.Equal(1, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Empty(saved.Set<SavedSkillExtraction>());
        Assert.Empty(saved.Set<TrainingLog>());
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("station")]
    [InlineData("cargo")]
    [InlineData("ship")]
    [InlineData("space")]
    [InlineData("singleton")]
    [InlineData("missing")]
    [InlineData("type")]
    public void RejectsInvalidInventoryAndBoardingStateWithoutMutation(string variant)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        long item = SkillExtractionTestData.Item(db);
        using (WorldDbContext world = new(db.Path))
        {
            var source = world.Set<OwnedItem>().Find(item)!;
            var ship = world.Ships.Single(s => s.Id == world.Pilots.Single(p => p.Id == SkillExtractionTestData.Pilot).ActiveShipId);
            switch (variant)
            {
                case "foreign":
                    source.OwnerId = 90000002;
                    break;
                case "station":
                    source.LocationId = HomeStationTestData.Other;
                    break;
                case "cargo":
                    source.FlagId = (int)InventoryFlag.Cargo;
                    source.LocationId = ship.Id;
                    break;
                case "ship":
                    ship.TypeId = 601;
                    break;
                case "space":
                    world.Add(
                        new FlightPosition
                        {
                            ShipId = ship.Id,
                            X = 1,
                            Y = 2,
                            Z = 3,
                        }
                    );
                    break;
                case "singleton":
                    source.Singleton = true;
                    break;
                case "missing":
                    world.Remove(source);
                    break;
                case "type":
                    source.TypeId = (int)SkillInjectorType.Large;
                    break;
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidSkillExtraction>(() =>
            SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, SkillExtractionTestData.Request())
        );
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(8_000_000, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, SkillExtractionTestData.Filler)!.Points);
        Assert.Empty(saved.Set<SavedSkillExtraction>());
        Assert.Empty(saved.Set<TrainingLog>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PausedAndActiveQueuedSkillsAndTheirPrerequisitesStayProtected(bool active)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Skill(db, 3327, 8000);
        SkillExtractionTestData.Requires(db, SkillExtractionTestData.Filler, 3327, 3);
        ImplantTestData.Training(db).Save(SkillExtractionTestData.Pilot, [new(3327, 4)], active);
        Assert.Throws<InvalidSkillExtraction>(() =>
            SkillExtractionTestData
                .Store(db)
                .Extract(
                    SkillExtractionTestData.Pilot,
                    SkillExtractionTestData.Item(db),
                    [new(3327, 1), new(SkillExtractionTestData.Filler, 499999)]
                )
        );
        var before = ImplantTestData.Training(db).Advance(SkillExtractionTestData.Pilot);
        var result = SkillExtractionTestData
            .Store(db)
            .Extract(SkillExtractionTestData.Pilot, SkillExtractionTestData.Item(db), SkillExtractionTestData.Request());
        Assert.Equal(before.Queue.Single().Target, result.Training.Queue.Single().Target);
        Assert.Equal(active, result.Training.Queue.Single().StartTime.HasValue);
    }

    [Fact]
    public void CompleteDependencyChainCanBeRemovedInOneExtractionAndSkillsRemainInjectedAtZero()
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Requires(db, ImplantTestData.Cybernetics, 3327, 3);
        SkillExtractionTestData.Skill(db, 3327, 8000);
        SkillExtractionTestData.Skill(db, ImplantTestData.Cybernetics, 492000);
        long item = SkillExtractionTestData.Item(db);
        SkillExtractionRequest[] request = [new(3327, 8000), new(ImplantTestData.Cybernetics, 492000)];
        SkillExtractionTestData.Store(db).Extract(SkillExtractionTestData.Pilot, item, request);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(0, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, 3327)!.Points);
        Assert.Equal(0, saved.Set<PilotSkill>().Find(SkillExtractionTestData.Pilot, ImplantTestData.Cybernetics)!.Points);
        Assert.Equal(2, saved.Set<TrainingLog>().Count());
    }

    public static IEnumerable<object[]> ImplantProfiles => ImplantTestData.Profiles.Select(p => new object[] { p.Type, p.Required });

    [Theory]
    [MemberData(nameof(ImplantProfiles))]
    public void EveryLearningImplantProtectsItsSkillFloorIncludingStoredCloneBodies(int type, int required)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Skill(db, ImplantTestData.Cybernetics, TrainedSkill.PointsForLevel(3, required));
        ImplantTestData.Store(db).Install(SkillExtractionTestData.Pilot, ImplantTestData.Item(db, type));
        using (WorldDbContext world = new(db.Path))
        {
            var body = world.Set<SavedJumpClone>().Single(c => c.PilotId == SkillExtractionTestData.Pilot && c.Active);
            body.Active = false;
            body.StationId = HomeStationTestData.Origin;
            world.SaveChanges();
            world.Add(new SavedJumpClone { PilotId = SkillExtractionTestData.Pilot, Active = true });
            world.SaveChanges();
        }
        Assert.Throws<InvalidSkillExtraction>(() =>
            SkillExtractionTestData
                .Store(db)
                .Extract(
                    SkillExtractionTestData.Pilot,
                    SkillExtractionTestData.Item(db),
                    [new(ImplantTestData.Cybernetics, 1), new(SkillExtractionTestData.Filler, 499999)]
                )
        );
        Assert.Single(ImplantTestData.Store(db).Read(SkillExtractionTestData.Pilot).Implants.Concat(ReadStoredImplants(db)));
    }

    private static InstalledImplant[] ReadStoredImplants(TestWorld db)
    {
        using WorldDbContext world = new(db.Path);
        return world.Set<SavedPilotImplant>().AsEnumerable().Select(i => i.Snapshot()).ToArray();
    }

    [Theory]
    [InlineData(1, InfomorphSkill.Psychology, 1, 1)]
    [InlineData(5, InfomorphSkill.Psychology, 5, 1)]
    [InlineData(6, InfomorphSkill.AdvancedPsychology, 1, 5)]
    [InlineData(10, InfomorphSkill.AdvancedPsychology, 5, 5)]
    [InlineData(11, InfomorphSkill.ElitePsychology, 1, 12)]
    [InlineData(15, InfomorphSkill.ElitePsychology, 5, 12)]
    public void InstalledCloneCountProtectsEachInfomorphCapacityTier(int count, InfomorphSkill skill, int level, int rank)
    {
        var clock = SkillInjectorTestData.Clock();
        using var db = SkillExtractionTestData.Create(clock);
        SkillExtractionTestData.Skill(db, (int)skill, TrainedSkill.PointsForLevel(rank, level));
        using (WorldDbContext world = new(db.Path))
        {
            for (int i = 0; i < count; i++)
            {
                world.Add(new SavedJumpClone { PilotId = SkillExtractionTestData.Pilot, StationId = HomeStationTestData.Origin });
            }
            world.SaveChanges();
        }
        Assert.Throws<InvalidSkillExtraction>(() =>
            SkillExtractionTestData
                .Store(db)
                .Extract(
                    SkillExtractionTestData.Pilot,
                    SkillExtractionTestData.Item(db),
                    [new((int)skill, 1), new(SkillExtractionTestData.Filler, 499999)]
                )
        );
    }
}
