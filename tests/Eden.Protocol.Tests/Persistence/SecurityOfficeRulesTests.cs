using Eden.Gameplay.Characters;

namespace Eden.Protocol.Tests.Persistence;

public sealed class SecurityOfficeRulesTests
{
    [Fact]
    public void AllFourBandsUseTheirOwnTags()
    {
        var plan = SecurityOfficeRules.Plan(-10, new(0, -10));
        Assert.Equal(0, plan.Status);
        Assert.Equal([new(33138, 4), new(33139, 6), new(33140, 6), new(33141, 4)], plan.Tags.ToArray());
    }

    [Theory]
    [InlineData(-0.09, 0, 0.41)]
    [InlineData(-0.09, 0.41, 0.41)]
    [InlineData(-0.5, 0, 0)]
    [InlineData(-1e-10, 0, 0.4999999999)]
    public void FinalTagGrantsItsFullHalfPoint(double start, double target, double expected)
    {
        var plan = SecurityOfficeRules.Plan(start, new(target, start));
        Assert.Equal(expected, plan.Status, 12);
        Assert.Equal(new(33141, 1), Assert.Single(plan.Tags));
    }

    [Fact]
    public void StaleAndUnreachableTargetsFail()
    {
        Assert.Throws<InvalidDataException>(() => SecurityOfficeRules.Plan(-2, new(-1, -3)));
        Assert.Throws<InvalidDataException>(() => SecurityOfficeRules.Plan(-0.2, new(0.4, -0.2)));
        Assert.Throws<InvalidDataException>(() => SecurityOfficeRules.Plan(0, new(0.4, 0)));
        Assert.Throws<InvalidDataException>(() => SecurityOfficeRules.Plan(-1, new(double.NaN, -1)));
    }

    [Theory]
    [InlineData(1000125, 0.001, true)]
    [InlineData(1000137, 0.449, true)]
    [InlineData(1000125, 0, false)]
    [InlineData(1000137, 0.45, false)]
    [InlineData(1000126, 0.2, false)]
    public void OfficeAvailabilityMatchesTheClient(int owner, double security, bool expected) =>
        Assert.Equal(expected, SecurityOfficeRules.Available(owner, security));
}
