using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;

namespace Eden.Protocol.Tests.Persistence;

public sealed class BoosterTimingTests
{
    private const long Start = 134380512000000000;

    private static long At(double seconds) => Start + TimeSpan.FromSeconds(seconds).Ticks;

    [Theory]
    [InlineData(AccountCloneGrade.Omega, 440, 30, 110)]
    [InlineData(AccountCloneGrade.Alpha, 940, 15, 55)]
    public void ExpiryChangesProgressWithinALevelAndPredictsFutureQueueEntries(AccountCloneGrade grade, int seconds, int early, int late)
    {
        TrainingSkill skill = new(0, 1, 40, 40, 5, []) { RateChanges = [new(At(60), 20, 20)] };
        var queue = TrainingRules.Plan(
            [new(3327, 1), new(3327, 2)],
            new(new Dictionary<int, TrainingSkill> { [3327] = skill }, grade),
            true,
            null,
            Start
        );
        Assert.Equal(At(seconds), queue[0].EndTime);
        Assert.Equal(early, queue[0].PointsAt(At(30)));
        Assert.Equal(late, queue[0].PointsAt(At(160)));
        Assert.Equal(250, queue[0].PointsAt(At(seconds)));
        Assert.Equal(0, queue[0].PointsAt(At(-1)));
        Assert.Equal(250, queue[0].PointsAt(At(seconds + 100)));
        Assert.Equal(queue[0].EndTime, queue[1].StartTime);
        Assert.Equal(grade == AccountCloneGrade.Omega ? At(2770) : At(5600), queue[1].EndTime);
    }

    [Fact]
    public void MultipleExpiriesKeepFractionalProgressAndUseTheNewRateAtTheExactBoundary()
    {
        TrainingSkill skill = new(0, 1, 40, 40, 5, []) { RateChanges = [new(At(30.5), 30, 30), new(At(60.5), 20, 20)] };
        var entry = Assert.Single(
            TrainingRules.Plan([new(3327, 1)], new(new Dictionary<int, TrainingSkill> { [3327] = skill }), true, null, Start)
        );
        // Alpha: .5 SP/s, then .375 SP/s, then .25 SP/s.
        Assert.Equal(At(954.5), entry.EndTime);
        Assert.Equal(15, entry.PointsAt(At(30.5)));
        Assert.Equal(26, entry.PointsAt(At(60.5)));
        Assert.Equal(27, entry.PointsAt(At(62.5)));
        Assert.Equal(26.5m, entry.Periods[2].StartPoints);
    }

    [Fact]
    public void LevelCompletionAtExpiryStartsTheNextLevelAtTheUnboostedRate()
    {
        TrainingSkill skill = new(190, 1, 40, 40, 5, []) { RateChanges = [new(At(60), 20, 20)] };
        var queue = TrainingRules.Plan(
            [new(3327, 1), new(3327, 2)],
            new(new Dictionary<int, TrainingSkill> { [3327] = skill }, AccountCloneGrade.Omega),
            true,
            null,
            Start
        );
        Assert.Equal(At(60), queue[0].EndTime);
        Assert.Equal(At(2390), queue[1].EndTime);
        Assert.Equal(.5m * 60, Assert.Single(queue[1].Periods).PointsPerMinute);
    }
}
