using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;

namespace Eden.Protocol.Tests.Information;

public sealed class InformationRuleTests
{
    [Theory]
    [InlineData(0, 500, 0)]
    [InlineData(100, 500, 100)]
    [InlineData(500, 500, 500)]
    [InlineData(501, 500, 500)]
    [InlineData(double.MaxValue, 500, 500)]
    [InlineData(double.MaxValue, double.MaxValue, InformationRules.MaximumDirectionalRange)]
    public void DirectionalRangeCapsFiniteRequestsWithoutIncreasingShortScans(double requested, double maximum, double expected) =>
        Assert.Equal(expected, InformationRules.DirectionalRange(requested, maximum));

    [Theory]
    [InlineData(-1, 500)]
    [InlineData(double.NaN, 500)]
    [InlineData(double.PositiveInfinity, 500)]
    [InlineData(500, -1)]
    [InlineData(500, double.NaN)]
    [InlineData(500, double.PositiveInfinity)]
    public void DirectionalRangeRejectsInvalidRequestsAndShipLimits(double requested, double maximum) =>
        Assert.Throws<InvalidDataException>(() => InformationRules.DirectionalRange(requested, maximum));

    [Fact]
    public void DirectionalConeIncludesExactEdgesAndRejectsInvalidRequests()
    {
        SpacePosition origin = SpacePosition.Zero;
        SpacePosition direction = new(1, 0, 0);
        Assert.True(InformationRules.InCone(origin, new(10, 10, 0), direction, Math.PI / 2, Math.Sqrt(200)));
        Assert.False(InformationRules.InCone(origin, new(10, 10.01, 0), direction, Math.PI / 2, 100));
        Assert.True(InformationRules.InCone(origin, new(-100, 0, 0), direction, Math.Tau, 100));
        Assert.False(InformationRules.InCone(origin, new(101, 0, 0), direction, Math.Tau, 100));
        Assert.Throws<InvalidDataException>(() => InformationRules.InCone(origin, origin, direction, double.NaN, 100));
        Assert.Throws<InvalidDataException>(() => InformationRules.InCone(origin, origin, origin, Math.Tau, 100));
        Assert.Throws<InvalidDataException>(() => InformationRules.InCone(origin, origin, new(double.MaxValue, 0, 0), Math.Tau, 100));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(360)]
    public void DirectionalNativeAnglesUseTheFullConeOpeningAndIncludeTheRangeBoundary(double degrees)
    {
        double angle = degrees * Math.PI / 180;
        SpacePosition direction = new(3, 0, 0);
        SpacePosition edge = new(100 * Math.Cos(angle / 2), 100 * Math.Sin(angle / 2), 0);
        Assert.True(InformationRules.InCone(SpacePosition.Zero, edge, direction, angle, 100.0000001));
        Assert.False(InformationRules.InCone(SpacePosition.Zero, new(100.1, 0, 0), direction, angle, 100));
        Assert.True(InformationRules.InCone(SpacePosition.Zero, SpacePosition.Zero, direction, angle, 0));
        Assert.True(
            InformationRules.InCone(
                SpacePosition.Zero,
                new(InformationRules.MaximumDirectionalRange, 0, 0),
                direction,
                angle,
                InformationRules.MaximumDirectionalRange
            )
        );
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-1, 100)]
    [InlineData(7, 100)]
    [InlineData(double.NaN, 100)]
    [InlineData(double.PositiveInfinity, 100)]
    [InlineData(1, -1)]
    [InlineData(1, double.NaN)]
    [InlineData(1, double.PositiveInfinity)]
    [InlineData(1, InformationRules.MaximumDirectionalRange + 1)]
    public void DirectionalInvalidAngleOrRangeFailsBeforeReadingAWorld(double angle, double range) =>
        Assert.Throws<InvalidDataException>(() =>
            InformationRules.InCone(SpacePosition.Zero, SpacePosition.Zero, new(1, 0, 0), angle, range)
        );

    [Theory]
    [InlineData(15, 30, 0.45)]
    [InlineData(35, -20, 0.15)]
    [InlineData(15, -20, 0)]
    [InlineData(85, 30, 1)]
    public void SalvageChanceUsesAdditiveUnpenalizedAccessBonuses(double bonus, double difficulty, double expected) =>
        Assert.Equal(expected, InformationRules.SalvageChance(bonus, difficulty), 10);

    [Fact]
    public void TractorMovesAtItsVelocityAndNeverCrossesTheCollectionBoundary()
    {
        Assert.Equal(new SpacePosition(7500, 0, 0), InformationRules.Pull(new(10000, 0, 0), SpacePosition.Zero, 1500, 500, 5));
        Assert.Equal(new SpacePosition(1500, 0, 0), InformationRules.Pull(new(10000, 0, 0), SpacePosition.Zero, 1500, 500, 100));
        Assert.Equal(new SpacePosition(1000, 0, 0), InformationRules.Pull(new(1000, 0, 0), SpacePosition.Zero, 1500, 500, 100));
    }

    [Theory]
    [InlineData(SalvageFamily.Amarr)]
    [InlineData(SalvageFamily.Minmatar)]
    [InlineData(SalvageFamily.Caldari)]
    [InlineData(SalvageFamily.Gallente)]
    [InlineData(SalvageFamily.RogueDrone)]
    public void SavedLootPolicyIsDeterministicAndAlwaysUsesTheFactionPool(SalvageFamily family)
    {
        Assert.Empty(SalvageLootRules.Roll(family, false, 30, 0));
        for (uint seed = 25; seed < 1000; seed++)
        {
            var result = SalvageLootRules.Roll(family, false, 30, seed);
            Assert.Equal(result, SalvageLootRules.Roll(family, false, 30, seed));
            foreach (var item in result)
            {
                Assert.Contains((SalvageMaterial)item.TypeId, SalvageLootRules.Pool(family));
                Assert.InRange(item.Quantity, 1, SalvageLootRules.SmallMaximum);
            }
        }
    }
}
