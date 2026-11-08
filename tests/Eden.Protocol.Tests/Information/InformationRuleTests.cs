using Eden.Gameplay.Flight;
using Eden.Gameplay.Information;

namespace Eden.Protocol.Tests.Information;

public sealed class InformationRuleTests
{
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
