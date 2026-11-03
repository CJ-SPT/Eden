using Eden.Gameplay.Mining;

namespace Eden.Protocol.Tests.Mining;

public sealed class MiningRulesTests
{
    private static readonly MiningLaser Laser = new(1, 483, 10000, 15000, 2.25, 5, 0, 2);
    private static readonly MiningRolls Rolls = new(.5, .5, .5, .5, .5);

    [Theory]
    [InlineData(.249999, 3)]
    [InlineData(.25, 2)]
    public void FractionalUnitsAreRoundedWithTheirActualProbability(double roll, int expected)
    {
        Assert.Equal(expected, MiningRules.Collect(100, 100, 1, Laser, new(1, true, Rolls with { Yield = roll })).Extracted);
    }

    [Fact]
    public void UniformRollsRecoverTheExpectedYieldWithoutPermanentFractionLoss()
    {
        int units = Enumerable
            .Range(0, 1000)
            .Sum(i => MiningRules.Collect(100, 100, 1, Laser, new(1, true, Rolls with { Yield = (i + .5) / 1000 })).Extracted);
        Assert.Equal(2250, units);
    }

    [Fact]
    public void CriticalBonusDoesNotConsumeOreOrIncreaseResidueAndBothRespectTheHold()
    {
        var laser = Laser with { Volume = 10, CriticalChance = 1, CriticalBonusYield = 2.5, ResidueChance = 1 };
        var result = MiningRules.Collect(100, 30, 1, laser, new(1, true, Rolls));
        Assert.Equal(new MiningRules.Collection(10, 20, 10, true), result);
    }

    [Fact]
    public void BonusAndResidueFractionsUseIndependentRoundingRolls()
    {
        var laser = Laser with { Volume = 3, CriticalChance = 1, CriticalBonusYield = .5, ResidueChance = 1, ResidueMultiplier = .5 };
        var result = MiningRules.Collect(100, 100, 1, laser, new(1, true, Rolls with { CriticalYield = .1, ResidueYield = .9 }));
        Assert.Equal(new MiningRules.Collection(3, 2, 1, false), result);
    }

    [Fact]
    public void ResidueCanDepleteARockWithoutAddingCargo()
    {
        var result = MiningRules.Collect(5, 100, 1, Laser with { Volume = 10, ResidueChance = 1 }, new(1, true, Rolls));
        Assert.Equal(new MiningRules.Collection(0, 0, 5, false), result);
    }

    [Fact]
    public void ManualStopsProrateYieldAndResidueWithoutRollingCriticalBonus()
    {
        var laser = Laser with { Volume = 10, CriticalChance = 1, ResidueChance = 1 };
        var result = MiningRules.Collect(100, 100, 1, laser, new(.25, false, Rolls with { Yield = .1 }));
        Assert.Equal(new MiningRules.Collection(3, 0, 3, false), result);
        Assert.Equal(new MiningRules.Collection(0, 0, 0, false), MiningRules.Collect(100, 100, 1, laser, new(0, false, Rolls)));
    }

    [Fact]
    public void RoundingNeverExceedsCargoOrRemainingOreAndHandlesLargeVolumes()
    {
        Assert.Equal(new MiningRules.Collection(1, 0, 0, true), MiningRules.Collect(100, 1, 1, Laser, new(1, true, Rolls)));
        Assert.Equal(new MiningRules.Collection(1, 0, 0, false), MiningRules.Collect(1, 100, 1, Laser, new(1, true, Rolls)));
        var large = Laser with { Volume = double.MaxValue };
        Assert.Equal(int.MaxValue, MiningRules.Collect(long.MaxValue, double.MaxValue, .1, large, new(1, true, Rolls)).Extracted);
    }

    [Fact]
    public void InvalidCycleAndRandomInputsAreRejected()
    {
        foreach (double invalid in new[] { -1, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<InvalidDataException>(() => MiningRules.Collect(100, 100, 1, Laser, new(invalid, false, Rolls)));
            Assert.Throws<InvalidDataException>(() =>
                MiningRules.Collect(100, 100, 1, Laser, new(1, true, Rolls with { Residue = invalid }))
            );
        }
        Assert.Throws<InvalidDataException>(() => MiningRules.Collect(100, 100, 1, Laser, new(1, true, Rolls with { Yield = 1 })));
        Assert.Throws<InvalidDataException>(() => MiningRules.Collect(100, 100, 1, Laser, new(.5, true, Rolls)));
    }
}
