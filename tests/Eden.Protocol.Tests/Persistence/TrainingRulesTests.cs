using Eden.Gameplay.Skills;

namespace Eden.Protocol.Tests.Persistence;

public sealed class TrainingRulesTests
{
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
