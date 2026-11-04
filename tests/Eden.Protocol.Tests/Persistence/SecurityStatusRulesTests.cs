using Eden.Gameplay.Characters;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SecurityStatusRulesTests
{
    [Theory]
    [InlineData(0, 0.005, 5, 0, 0.0005)]
    [InlineData(0, 0.005, 5, 5, 0.000625)]
    [InlineData(-5, 1, 5, 0, -4.85)]
    [InlineData(2.9999, 1, 3, 5, 3)]
    [InlineData(3, 1, 3, 5, 3)]
    [InlineData(5, 1.6, 5, 5, 5)]
    [InlineData(10, 1, 5, 0, 10)]
    [InlineData(-0.001, 1, 0, 0, 0)]
    [InlineData(0, 0, 5, 0, 0)]
    public void CatalogPercentagesSkillsAndCapsProduceExpectedBoundedGains(
        double before,
        double percent,
        double cap,
        int skill,
        double after
    )
    {
        var result = SecurityStatusRules.Apply(before, SecurityStatusRules.Reward(percent, cap, skill, 5));
        Assert.Equal(after, result.After, 10);
        Assert.Equal(after == before ? 0 : (after - before) / (10 - before), result.Modification, 10);
    }

    [Fact]
    public void NonfiniteInvalidRanksAndOutOfRangeValuesCannotEnterAPendingReward()
    {
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, -10.1, 10.1 })
        {
            Assert.Throws<InvalidDataException>(() => SecurityStatusRules.Check(value));
        }
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, -1, 101 })
        {
            Assert.Throws<InvalidDataException>(() => SecurityStatusRules.Reward(value, 5, 0, 5));
            Assert.Throws<InvalidDataException>(() => SecurityStatusRules.Reward(1, 5, 1, value));
        }
        Assert.Throws<InvalidDataException>(() => SecurityStatusRules.Reward(1, 5, 6, 5));
    }
}
