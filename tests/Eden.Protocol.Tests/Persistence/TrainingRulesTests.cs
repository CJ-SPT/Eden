using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;

namespace Eden.Protocol.Tests.Persistence;

public sealed class TrainingRulesTests
{
    [Theory]
    [InlineData(AccountCloneGrade.Alpha, 1, 1000)]
    [InlineData(AccountCloneGrade.Alpha, 2.5, 400)]
    [InlineData(AccountCloneGrade.Omega, 0.1, 5000)]
    [InlineData(AccountCloneGrade.Omega, 100, 5)]
    public void TrainingMultiplierScalesRateWithoutChangingLevelThreshold(AccountCloneGrade grade, decimal multiplier, int seconds)
    {
        TrainingInputs inputs = new(new Dictionary<int, TrainingSkill> { [1] = new(0, 1, 20, 20, 5, []) }, grade, 0, multiplier);
        var entry = Assert.Single(TrainingRules.Plan([new(1, 1)], inputs, true, null, 0));
        Assert.Equal(TimeSpan.FromSeconds(seconds).Ticks, entry.EndTime);
        Assert.Equal(125, entry.PointsAt(entry.EndTime!.Value / 2));
        Assert.Equal(250, entry.EndPoints);
    }

    [Fact]
    public void TrainingMultiplierScalesProgressBeforeAndAfterBoosterExpiry()
    {
        long expiry = TimeSpan.FromMinutes(2).Ticks;
        TrainingSkill boosted = new(0, 1, 30, 30, 5, []) { RateChanges = [new(expiry, 20, 20)] };
        TrainingInputs inputs = new(new Dictionary<int, TrainingSkill> { [1] = boosted }, SpeedMultiplier: 2);
        var entry = Assert.Single(TrainingRules.Plan([new(1, 1)], inputs, true, null, 0));
        Assert.Equal(2, entry.Periods.Count);
        Assert.Equal(45, entry.Periods[0].PointsPerMinute);
        Assert.Equal(30, entry.Periods[1].PointsPerMinute);
        Assert.Equal(90, entry.PointsAt(expiry));
        Assert.Equal(120, entry.PointsAt(expiry + TimeSpan.TicksPerMinute));
        Assert.Equal(expiry + TimeSpan.FromSeconds(320).Ticks, entry.EndTime);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 5)]
    [InlineData(3, 3)]
    public void LevelThresholdsRoundUpAndRecognizeThePreviousPoint(double rank, int level)
    {
        long threshold = TrainedSkill.PointsForLevel(rank, level);
        Assert.Equal(level, new TrainedSkill(1, threshold, rank).Level);
        Assert.Equal(level - 1, new TrainedSkill(1, threshold - 1, rank).Level);
    }

    [Fact]
    public void PrerequisitesUseProjectedLevelsAndKeepARunningHead()
    {
        const int prerequisite = 1;
        const int dependent = 2;
        TrainingInputs inputs = new(
            new Dictionary<int, TrainingSkill>
            {
                [prerequisite] = new(0, 1, 20, 20, 5, []),
                [dependent] = new(0, 1, 20, 20, 5, [new(prerequisite, 1)]),
            }
        );
        TrainingEntry running = new(new(prerequisite, 1), 0, 250, 100, 200);
        var result = TrainingRules.Plan([new(prerequisite, 1), new(dependent, 1)], inputs, true, running, 150);
        Assert.Same(running, result[0]);
        Assert.Equal(200, result[1].StartTime);
        Assert.Equal(
            "QueueSkillPrerequisitesNotMet",
            Assert.Throws<InvalidTrainingQueue>(() => TrainingRules.Plan([new(dependent, 1)], inputs, true, null, 150)).Message
        );
    }
}
