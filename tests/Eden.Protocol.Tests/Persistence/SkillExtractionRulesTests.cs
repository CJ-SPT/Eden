using Eden.Gameplay.Docking;
using Eden.Gameplay.Skills;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SkillExtractionRulesTests
{
    private static TrainingInputs Inputs(AccountCloneGrade grade = AccountCloneGrade.Omega) =>
        new(
            new Dictionary<int, TrainingSkill>
            {
                [1] = new(5_500_000, 1000, 20, 20, 0, []),
                [2] = new(500_000, 3, 20, 20, 4, [new(3, 3)]),
                [3] = new(8000, 1, 20, 20, 4, []),
            },
            grade
        );

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(499999)]
    [InlineData(500001)]
    [InlineData(long.MaxValue)]
    public void RequiresExactlyOnePositiveBucket(long amount) =>
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(1, amount)], Inputs(), [], []));

    [Fact]
    public void RejectsEmptyDuplicateUnknownAndOverdrawnSkills()
    {
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([], Inputs(), [], []));
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(1, 250000), new(1, 250000)], Inputs(), [], []));
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(99, 500000)], Inputs(), [], []));
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(3, 500000)], Inputs(), [], []));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinalDependencySnapshotIsIndependentOfRequestOrder(bool reverse)
    {
        SkillExtractionRequest[] request = [new(3, 8000), new(2, 492000)];
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan(request, Inputs(), [], []));
        var inputs = Inputs() with
        {
            Skills = new Dictionary<int, TrainingSkill>(Inputs().Skills) { [2] = Inputs().Skills[2] with { Points = 492000 } },
        };
        if (reverse)
        {
            Array.Reverse(request);
        }
        Assert.All(SkillExtractionRules.Plan(request, inputs, [], []), a => Assert.Equal(0, a.After));
    }

    [Fact]
    public void AlphaCanRemoveOnlyProgressAboveItsAllowedLevel()
    {
        var inputs = Inputs(AccountCloneGrade.Alpha);
        long floor = TrainedSkill.PointsForLevel(3, 4);
        var accepted = SkillExtractionRules.Plan([new(2, 500000 - floor), new(1, floor)], inputs, [], []);
        Assert.Equal(floor, accepted.Single(a => a.TypeId == 2).After);
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(2, 500000)], inputs, [], []));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void QueuedSkillsAreLockedAtEveryTargetLevel(int level) =>
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(1, 500000)], Inputs(), [new(1, level)], []));

    [Fact]
    public void OvertrainedSkillsAndInvalidSavedStateCannotBeExtracted()
    {
        var skills = new Dictionary<int, TrainingSkill>(Inputs().Skills) { [1] = Inputs().Skills[1] with { Rank = 1 } };
        Assert.Throws<InvalidSkillExtraction>(() => SkillExtractionRules.Plan([new(1, 500000)], new(skills), [], []));
        skills[1] = skills[1] with { Rank = double.NaN };
        Assert.Throws<InvalidDataException>(() => SkillExtractionRules.Plan([new(1, 500000)], new(skills), [], []));
    }

    [Fact]
    public void AnUntrainedQueuedDependentStillProtectsItsPrerequisite()
    {
        var skills = new Dictionary<int, TrainingSkill>(Inputs().Skills) { [2] = Inputs().Skills[2] with { Points = 0 } };
        Assert.Throws<InvalidSkillExtraction>(() =>
            SkillExtractionRules.Plan([new(3, 1), new(1, 499999)], new(skills, AccountCloneGrade.Omega), [new(2, 1)], [])
        );
        Assert.Equal(2, SkillExtractionRules.Plan([new(3, 1), new(1, 499999)], new(skills, AccountCloneGrade.Omega), [], []).Count);
    }
}
