using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Skills;
using Eden.Server;
using Eden.World.Characters;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class BoosterTests
{
    public static IEnumerable<object[]> Profiles => BoosterTestData.Profiles.Select(p => new object[] { p.Type });

    [Theory]
    [MemberData(nameof(Profiles))]
    public void EveryPublishedLearningProfileConsumesOneUnitAndPersistsItsFixedLifetime(int type)
    {
        var profile = BoosterTestData.Profiles.Single(p => p.Type == type);
        var clock = CalendarTestData.Clock();
        if (profile.LastDate is double last)
        {
            clock.UtcNow = new DateTimeOffset(DateTime.UnixEpoch.AddDays(last).AddHours(-1));
        }
        using var db = BoosterTestData.Create(clock, types: [type]);
        using LiveWorld live = new(new(db.Path), clock, _ => { });
        long item = BoosterTestData.Item(db, type, 2);
        var result = BoosterTestData.Consume(db, item);
        var booster = Assert.Single(result.Snapshot.Boosters);
        Assert.Equal(type, booster.TypeId);
        Assert.Equal(profile.Slot, booster.Slot);
        Assert.Equal(20 + profile.Bonus, result.Snapshot.Attributes.Perception);
        long remainingAge = profile.AgeHours > 0 ? TimeSpan.FromHours(profile.AgeHours - 1).Ticks : long.MaxValue;
        long duration = profile.DurationMs > 0 ? checked(profile.DurationMs * 2 * TimeSpan.TicksPerMillisecond) : remainingAge;
        Assert.Equal(clock.Now + Math.Min(duration, remainingAge), booster.ExpiresAt);
        Assert.Equal(1, result.Change.Item.Quantity);
        Assert.NotEqual(item, booster.ItemId);
        Assert.Throws<InvalidDataException>(() => BoosterTestData.Consume(db, item));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        Assert.Equal(booster, Assert.Single(reopened.GetRequiredService<IPilotBoosters>().Read(BoosterTestData.Pilot).Boosters));
        clock.UtcNow = new DateTimeOffset(DateTime.FromFileTimeUtc(booster.ExpiresAt));
        Assert.Empty(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters);
        Assert.Equal(
            CharacterAttributes.Default,
            db.Services.GetRequiredService<IPilotCharacterSheet>().ReadEffectiveAttributes(BoosterTestData.Pilot)
        );
    }

    [Fact]
    public void TrainingPredictsExpiryAndRetainsExactProgressAcrossPauseAndOfflineRestart()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        BoosterTestData.ShortLifetime(db);
        BoosterTestData.Consume(db, BoosterTestData.Item(db));
        var training = ImplantTestData.Training(db);
        var state = training.Save(BoosterTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(440).Ticks, state.Queue[0].EndTime);
        Assert.Equal(2, state.Queue[0].Periods.Count);
        Assert.Equal(45, state.Queue[0].PointsAt(clock.Now + TimeSpan.FromSeconds(60).Ticks));
        Assert.Equal(90, state.Queue[0].PointsAt(clock.Now + TimeSpan.FromSeconds(120).Ticks));
        clock.AdvanceWallTime(TimeSpan.FromSeconds(200));
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path), clock);
        var restored = reopened.GetRequiredService<IPilotTraining>().Advance(BoosterTestData.Pilot);
        Assert.Equal(state.Queue, restored.Queue);
        Assert.Equal(130, restored.Queue[0].PointsAt(clock.Now));
        var paused = reopened.GetRequiredService<IPilotTraining>().Pause(BoosterTestData.Pilot);
        Assert.Equal(130, paused.Queue[0].StartPoints);
        Assert.Null(paused.Queue[0].StartTime);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        var resumed = training.Save(BoosterTestData.Pilot, [new(3327, 1), new(3327, 2)], true);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(240).Ticks, resumed.Queue[0].EndTime);
        clock.AdvanceWallTime(TimeSpan.FromDays(1));
        Assert.Empty(training.Advance(BoosterTestData.Pilot).Queue);
        Assert.Empty(training.Advance(BoosterTestData.Pilot).ChangedSkills);
        Assert.Equal(2, training.History(BoosterTestData.Pilot, 100).Count(e => e.EventId == 53));
    }

    [Fact]
    public void ApplyingFreePointsUsesCurrentRatePeriodsAndReplansTheRemainingExpiry()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        BoosterTestData.ShortLifetime(db);
        BoosterTestData.Consume(db, BoosterTestData.Item(db));
        ImplantTestData.Training(db).Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(new SavedFreeSkillPoints { PilotId = BoosterTestData.Pilot, Points = 50 });
            world.SaveChanges();
        }
        clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        var result = db.Services.GetRequiredService<IPilotFreeSkillPoints>().Apply(BoosterTestData.Pilot, new FreePointRequest.Queue());
        var entry = Assert.Single(result.Training.Queue);
        Assert.Equal(95, entry.StartPoints);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(280).Ticks, entry.EndTime);
        Assert.Equal(2, entry.Periods.Count);
    }

    [Fact]
    public void AcceleratorsSurviveCloneJumpsAndHomeCloneDestructionWhileImplantsFollowTheirBody()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock, clones: true);
        var booster = Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db)).Snapshot.Boosters);
        ImplantTestData.Store(db).Install(BoosterTestData.Pilot, ImplantTestData.Item(db));
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(BoosterTestData.Pilot);
        var body = Assert.Single(clones.Read(BoosterTestData.Pilot).Clones);
        long pod = HomeStationTestData.MakeActiveCapsule(db, HomeStationTestData.Origin);
        var jumped = clones.Jump(BoosterTestData.Pilot, pod, body.StationId, body.Id, JumpCloneRules.FeeCents);
        Assert.Equal(booster, Assert.Single(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters));
        Assert.Equal(30, db.Services.GetRequiredService<IPilotCharacterSheet>().ReadEffectiveAttributes(BoosterTestData.Pilot).Perception);
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        db.Services.GetRequiredService<IPilotHomeStation>().SelfDestruct(BoosterTestData.Pilot, jumped.Pilot.ShipId);
        Assert.Equal(booster, Assert.Single(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters));
    }

    [Fact]
    public void CloneSwitchDuringBoostedTrainingUsesTheOutgoingRateAndPreservesTheBoosterDeadline()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock, clones: true);
        BoosterTestData.ShortLifetime(db);
        var booster = Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db)).Snapshot.Boosters);
        ImplantTestData.Store(db).Install(BoosterTestData.Pilot, ImplantTestData.Item(db));
        var clones = db.Services.GetRequiredService<IPilotJumpClones>();
        clones.Install(BoosterTestData.Pilot);
        var body = Assert.Single(clones.Read(BoosterTestData.Pilot).Clones);
        long pod = HomeStationTestData.MakeActiveCapsule(db, HomeStationTestData.Origin);
        var training = ImplantTestData.Training(db);
        training.Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        var jumped = clones.Jump(BoosterTestData.Pilot, pod, body.StationId, body.Id, JumpCloneRules.FeeCents);
        var entry = Assert.Single(training.Advance(BoosterTestData.Pilot).Queue);
        Assert.Equal(48, entry.StartPoints);
        Assert.Equal(booster.ExpiresAt + TimeSpan.FromSeconds(314).Ticks, entry.EndTime);
        HomeStationTestData.Travel(db, HomeStationTestData.Other);
        db.Services.GetRequiredService<IPilotHomeStation>().SelfDestruct(BoosterTestData.Pilot, jumped.Pilot.ShipId);
        Assert.Equal(booster, Assert.Single(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters));
        entry = Assert.Single(training.Advance(BoosterTestData.Pilot).Queue);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(90));
        Assert.Equal(108, entry.PointsAt(clock.Now));
    }

    [Fact]
    public void BiologyIsCapturedAtConsumptionAndAlphaUsesItsEffectiveSkillCap()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        ImplantTestData.Alpha(db);
        var booster = Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db)).Snapshot.Boosters);
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(2304).Ticks, booster.ExpiresAt);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.SaveChanges();
        }
        Assert.Equal(booster.ExpiresAt, Assert.Single(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters).ExpiresAt);
    }

    [Fact]
    public void CatalogProfilesWithoutPrerequisitesDoNotInventAScienceRequirement()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock, types: [59803]);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<PilotSkill>().Find(BoosterTestData.Pilot, BoosterTestData.Science)!.Points = 0;
            world.SaveChanges();
        }
        Assert.Single(BoosterTestData.Consume(db, BoosterTestData.Item(db, 59803)).Snapshot.Boosters);
    }

    [Fact]
    public void DifferentSlotsCombineBonusesAndExpireIndependentlyInsideTheSameLevel()
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock, types: [BoosterTestData.Master, 59803]);
        BoosterTestData.ShortLifetime(db);
        BoosterTestData.ShortLifetime(db, 60, 59803);
        BoosterTestData.Consume(db, BoosterTestData.Item(db));
        var combined = BoosterTestData.Consume(db, BoosterTestData.Item(db, 59803));
        Assert.Equal(2, combined.Snapshot.Boosters.Count);
        Assert.Equal(40, combined.Snapshot.Attributes.Perception);
        var state = ImplantTestData.Training(db).Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        var entry = Assert.Single(state.Queue);
        Assert.Equal(clock.Now + TimeSpan.FromSeconds(410).Ticks, entry.EndTime);
        Assert.Equal(3, entry.Periods.Count);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(90));
        Assert.Equal(82, entry.PointsAt(clock.Now));
        Assert.Single(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        Assert.Equal(120, entry.PointsAt(clock.Now));
        Assert.Empty(BoosterTestData.Store(db).Read(BoosterTestData.Pilot).Boosters);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("remote")]
    [InlineData("location")]
    [InlineData("skill")]
    [InlineData("spaceHangar")]
    public void RejectedConsumptionLeavesItemsAndActiveTrainingUnchanged(string reason)
    {
        var clock = CalendarTestData.Clock();
        using var db = BoosterTestData.Create(clock);
        long item = BoosterTestData.Item(db, quantity: 2);
        using (WorldDbContext world = new(db.Path))
        {
            if (reason == "foreign")
            {
                world.Set<OwnedItem>().Find(item)!.OwnerId = 90000002;
            }
            if (reason == "remote")
            {
                world.Set<OwnedItem>().Find(item)!.LocationId = HomeStationTestData.Other;
            }
            if (reason == "skill")
            {
                world.Set<PilotSkill>().Find(BoosterTestData.Pilot, BoosterTestData.Science)!.Points = 0;
            }
            world.SaveChanges();
        }
        if (reason == "spaceHangar")
        {
            db.Store.SetPosition(db.Store.FindPilot(1000001)!.ShipId, SpacePosition.Zero);
        }
        var state = ImplantTestData.Training(db).Save(BoosterTestData.Pilot, [new(3327, 1)], true);
        clock.AdvanceWallTime(TimeSpan.FromSeconds(60));
        Assert.Throws<InvalidDataException>(() =>
            BoosterTestData
                .Store(db)
                .Consume(BoosterTestData.Pilot, item, reason == "location" ? HomeStationTestData.Other : HomeStationTestData.Origin)
        );
        Assert.Equal(state.Queue, ImplantTestData.Training(db).Advance(BoosterTestData.Pilot).Queue);
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<OwnedItem>().Find(item)!.Quantity);
        Assert.Equal(0, saved.Set<PilotSkill>().Find(BoosterTestData.Pilot, 3327)!.Points);
        Assert.Empty(saved.Set<SavedBooster>());
        Assert.Empty(saved.Set<SavedBoosterState>());
    }
}
