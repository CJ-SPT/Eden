using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.World.Characters;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class CloneGradeTests
{
    [Theory]
    [InlineData(AccountCloneGrade.Alpha, 1000)]
    [InlineData(AccountCloneGrade.Omega, 500)]
    public void CloneGradeControlsTheActualTrainingRate(AccountCloneGrade grade, int seconds)
    {
        TrainingInputs input = new(new Dictionary<int, TrainingSkill> { [1] = new(0, 1, 20, 20, 5, []) }, grade);
        var entry = Assert.Single(TrainingRules.Plan([new(1, 1)], input, true, null, 100));
        Assert.Equal(100 + seconds * TimeSpan.TicksPerSecond, entry.EndTime);
    }

    [Fact]
    public void OmegaCanTrainRestrictedSkillsAboveAlphaPointsWithoutBypassingPrerequisites()
    {
        Dictionary<int, TrainingSkill> skills = new() { [1] = new(0, 1, 20, 20, 0, []), [2] = new(5000000, 100, 20, 20, 5, []) };
        Assert.Equal(
            "SkillInQueueRequiresOmegaCloneState",
            Assert.Throws<InvalidTrainingQueue>(() => TrainingRules.Plan([new(1, 1)], new(skills), true, null, 0)).Message
        );
        Assert.Single(TrainingRules.Plan([new(1, 1)], new(skills, AccountCloneGrade.Omega), true, null, 0));
        skills[1] = skills[1] with { AlphaLevel = 5 };
        Assert.Equal(
            "SkillInQueueOverAlphaSpTrainingSize",
            Assert.Throws<InvalidTrainingQueue>(() => TrainingRules.Plan([new(1, 1)], new(skills), true, null, 0)).Message
        );
        skills[1] = skills[1] with { Prerequisites = [new(2, 5)] };
        Assert.Equal(
            "QueueSkillPrerequisitesNotMet",
            Assert
                .Throws<InvalidTrainingQueue>(() => TrainingRules.Plan([new(1, 1)], new(skills, AccountCloneGrade.Omega), true, null, 0))
                .Message
        );
    }

    [Fact]
    public void SavedAccountGradeReachesTheNativeLoginAndTrainingWithoutChangingAnotherAccount()
    {
        using TestWorld db = new();
        TestClock clock = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<Account>().Find(1000001L)!.CloneGrade = AccountCloneGrade.Omega;
            world.Set<PilotSkill>().Find(90000001L, 3327)!.Points = 0;
            world.SaveChanges();
        }
        var pilot = db.Store.FindPilot(1000001)!;
        Assert.Equal(AccountCloneGrade.Omega, pilot.Account.CloneGrade);
        Assert.Equal(AccountCloneGrade.Omega, db.Store.ReadAccount(1000001).CloneGrade);
        Assert.Equal(AccountCloneGrade.Alpha, db.Store.ReadAccount(1000002).CloneGrade);
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, clock, new(), []),
            1,
            SignedSettings.Empty
        );
        var handler = db.Services.GetRequiredService<AccountCompatibilityHandler>();
        Assert.Equal(
            1,
            WireAssertions.Materialize(handler.Call(context, "subscriptionMgr", "GetCloneGrade", Array.Empty<object?>(), null))
        );
        var entry = Assert.Single(new PilotTrainingStore(new(db.Path), clock).Save(pilot.PilotId, [new(3327, 1)], true).Queue);
        Assert.Equal(clock.Now + 500 * TimeSpan.TicksPerSecond, entry.EndTime);
        WorldInitialization.Initialize(new(db.Path), clock);
        Assert.Equal(AccountCloneGrade.Omega, db.Store.ReadAccount(1000001).CloneGrade);
    }

    [Fact]
    public void VersionThirtySixMigrationPreservesAlphaAsTheDefault()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(TestWorld.DropCloneGradeSchema + "PRAGMA user_version=36");
        }
        WorldInitialization.Initialize(new(db.Path));
        Assert.Equal(AccountCloneGrade.Alpha, db.Store.ReadAccount(1000001).CloneGrade);
        Assert.Equal(AccountCloneGrade.Alpha, db.Store.ReadAccount(1000002).CloneGrade);
    }
}
