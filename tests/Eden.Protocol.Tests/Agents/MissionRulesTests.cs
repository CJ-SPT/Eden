using Eden.Gameplay.Agents;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionRulesTests
{
    [Theory]
    [InlineData(1, 100000, 300000, 500, 1500)]
    [InlineData(2, 400000, 600000, 2000, 2500)]
    [InlineData(3, 1200000, 1800000, 3000, 4000)]
    [InlineData(4, 4000000, 6000000, 6000, 11000)]
    [InlineData(5, 10000000, 20000000, 12000, 25000)]
    public void RewardsRespectAgreedBaseBandsAndApplySkillBonuses(int level, long minimum, long maximum, int minLp, int maxLp)
    {
        var low = MissionRules.Reward(level, 0, 0, 0, 0, false);
        var high = MissionRules.Reward(level, 1000, 0, 0, 0, false);
        Assert.Equal(minimum * 100, low.IskCents);
        Assert.Equal(maximum * 100, high.IskCents);
        Assert.Equal(minLp, low.LoyaltyPoints);
        Assert.Equal(maxLp, high.LoyaltyPoints);
        var skilled = MissionRules.Reward(level, 1000, 5, 5, 5, false);
        Assert.Equal(maximum * 125, skilled.IskCents);
        Assert.Equal(maxLp * 3 / 2, skilled.LoyaltyPoints);
        Assert.Equal(high.CorporationFraction * 1.25, skilled.CorporationFraction, 12);
        Assert.Equal(0, skilled.FactionFraction);
    }

    [Theory]
    [InlineData(1, 0.0005, 0.001, 0.015, 0.02)]
    [InlineData(2, 0.001, 0.003, 0.03, 0.04)]
    [InlineData(3, 0.002, 0.006, 0.05, 0.065)]
    [InlineData(4, 0.008, 0.015, 0.075, 0.093)]
    public void StandingRangesUseFractionsAndStorylinesDoNotPayLp(
        int level,
        double lowCorp,
        double highCorp,
        double lowFaction,
        double highFaction
    )
    {
        var low = MissionRules.Reward(level, 0, 0, 0, 0, true);
        var high = MissionRules.Reward(level, 1000, 0, 0, 0, true);
        Assert.Equal(lowCorp, low.CorporationFraction, 12);
        Assert.Equal(highCorp, high.CorporationFraction, 12);
        Assert.Equal(lowFaction, low.FactionFraction, 12);
        Assert.Equal(highFaction, high.FactionFraction, 12);
        Assert.Equal(0, high.LoyaltyPoints);
        Assert.Equal(0.1, MissionRules.Gain(0, 0.01), 12);
        Assert.Equal(5.05, MissionRules.Gain(5, 0.01), 12);
        Assert.Equal(10, MissionRules.Gain(10, high.FactionFraction));
    }

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 3)]
    [InlineData(4, 5)]
    [InlineData(5, 7)]
    public void AccessRequiresAnyQualifyingStandingAndAllThreeAboveNegativeTwo(int level, double threshold)
    {
        Assert.True(MissionRules.Access(level, threshold, 0, 0, true).Allowed);
        Assert.True(MissionRules.Access(level, 0, threshold, 0, true).Allowed);
        Assert.True(MissionRules.Access(level, 0, 0, threshold, true).Allowed);
        Assert.False(MissionRules.Access(level, threshold - 0.00001, 0, 0, true).Allowed);
        Assert.False(MissionRules.Access(level, -2, threshold, threshold, true).Allowed);
        Assert.False(MissionRules.Access(level, threshold, -2, threshold, true).Allowed);
        Assert.False(MissionRules.Access(level, threshold, threshold, -2, true).Allowed);
        Assert.True(MissionRules.Access(level, -1.99999, threshold, 0, true).Allowed);
        Assert.Equal(level < 4, MissionRules.Access(level, threshold, threshold, threshold, false).Allowed);
    }

    [Fact]
    public void LevelOneRecoveryAndMutuallyExclusiveSocialSkillsMatchNativeAccess()
    {
        Assert.True(MissionRules.Access(1, -10, -10, -10, false).Allowed);
        Assert.Equal(1.2, MissionRules.EffectiveStanding(0, 3, 5, 5, false), 12);
        Assert.Equal(2, MissionRules.EffectiveStanding(0, 3, 5, 5, true), 12);
        Assert.Equal(-0.4, MissionRules.EffectiveStanding(-3, 5, 5, 5, false), 12);
        Assert.Equal(-3, MissionRules.EffectiveStanding(-3, 5, 5, 5, false, false));
    }

    [Fact]
    public void InvalidLevelsRatesAndNonFiniteStandingsAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => MissionRules.Band(0));
        Assert.Throws<InvalidDataException>(() => MissionRules.Reward(5, 0, 0, 0, 0, true));
        Assert.Throws<InvalidDataException>(() => MissionRules.Reward(1, 1001, 0, 0, 0, false));
        Assert.Throws<InvalidDataException>(() => MissionRules.Reward(1, 0, 6, 0, 0, false));
        Assert.Throws<InvalidDataException>(() => MissionRules.Access(2, double.NaN, 0, 0, true));
        Assert.Throws<InvalidDataException>(() => MissionRules.Gain(0, double.PositiveInfinity));
    }
}
