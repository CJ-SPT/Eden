using Eden.Gameplay.Sovereignty;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class SovereigntyRulesTests
{
    [Theory]
    [InlineData(30003995, 10000049, -0.4, null, true, true)]
    [InlineData(30003995, 10000049, 0, 0L, true, true)]
    [InlineData(30003995, 10000049, 0.1, null, true, false)]
    [InlineData(30003995, 10000049, -0.4, 500010L, true, false)]
    [InlineData(30003995, 10000049, -0.4, null, false, false)]
    [InlineData(30003995, 10000004, -0.4, null, true, false)]
    [InlineData(30003995, 10000017, -0.4, null, true, false)]
    [InlineData(30003995, 10000019, -0.4, null, true, false)]
    [InlineData(30003995, 10000070, -0.4, null, true, false)]
    [InlineData(30100000, 10000049, -1, null, true, false)]
    [InlineData(31000001, 11000001, -1, null, true, false)]
    [InlineData(32000001, 12000001, -1, null, true, false)]
    [InlineData(29999999, 10000049, -1, null, true, false)]
    [InlineData(30003995, 0, -1, null, true, false)]
    [InlineData(30003995, 10000049, double.NaN, null, true, false)]
    [InlineData(30003995, 10000049, double.NegativeInfinity, null, true, false)]
    public void ClaimabilityExcludesNpcAndSpecialSpace(
        int system,
        int region,
        double security,
        long? faction,
        bool anchoring,
        bool expected
    ) => Assert.Equal(expected, SovereigntyRules.CanClaim(new(system, region, security, faction, anchoring)));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 1)]
    [InlineData(20, 1)]
    [InlineData(21, 2)]
    [InlineData(34, 2)]
    [InlineData(35, 3)]
    [InlineData(64, 3)]
    [InlineData(65, 4)]
    [InlineData(99, 4)]
    [InlineData(100, 5)]
    [InlineData(1000, 5)]
    public void StrategicAgeUsesCompletedHoldThresholds(int days, int expected)
    {
        const long claimedAt = 1;
        long now = claimedAt + days * TimeSpan.TicksPerDay;
        Assert.Equal(expected, SovereigntyRules.StrategicIndex(claimedAt, now));
        if (days is 7 or 21 or 35 or 65 or 100)
        {
            Assert.Equal(expected - 1, SovereigntyRules.StrategicIndex(claimedAt, now - 1));
        }
    }

    [Fact]
    public void StrategicAgeHandlesBackwardsClocksAndRejectsInvalidTimes()
    {
        Assert.Equal(0, SovereigntyRules.StrategicIndex(long.MaxValue, 0));
        Assert.Equal(5, SovereigntyRules.StrategicIndex(1, long.MaxValue));
        Assert.Throws<InvalidDataException>(() => SovereigntyRules.StrategicIndex(0, 1));
        Assert.Throws<InvalidDataException>(() => SovereigntyRules.StrategicIndex(-1, 1));
        Assert.Throws<InvalidDataException>(() => SovereigntyRules.StrategicIndex(1, -1));
    }

    [Theory]
    [InlineData(0, 0, 0, false, "1.0")]
    [InlineData(0, 0, 5, false, "2.0")]
    [InlineData(3, 1, 5, false, "4.3")]
    [InlineData(4, 2, 2, false, "4.9")]
    [InlineData(3, 3, 5, false, "5.4")]
    [InlineData(5, 5, 5, false, "6.0")]
    [InlineData(0, 0, 0, true, "3.0")]
    [InlineData(3, 1, 5, true, "6.0")]
    public void DefenseMultiplierCombinesIndicesAndCapsCapitalBonus(
        int military,
        int industrial,
        int strategic,
        bool capital,
        string expected
    ) =>
        Assert.Equal(
            decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            SovereigntyRules.DefenseMultiplier(new(military, industrial, strategic), capital)
        );

    [Theory]
    [InlineData(-1, 0, 0)]
    [InlineData(6, 0, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 6, 0)]
    [InlineData(0, 0, -1)]
    [InlineData(0, 0, 6)]
    public void DefenseMultiplierRejectsInvalidIndices(int military, int industrial, int strategic) =>
        Assert.Throws<InvalidDataException>(() => SovereigntyRules.DefenseMultiplier(new(military, industrial, strategic)));
}
