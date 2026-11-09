using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Flight;

public sealed class DungeonObjectGeometryTests
{
    [Theory]
    [InlineData(25, 1, 70000, 0.1, 83.666)]
    [InlineData(2, 1500, 70000, 0, 1500)]
    [InlineData(2, 6000, 6000, 0, 6000)]
    [InlineData(25, 1, 0, 0.1, 1)]
    public void ContentBoundsAndOreQuantitiesAreNotPhysicalRadii(
        int category,
        double typeRadius,
        double sourceRadius,
        double unitVolume,
        double expected
    )
    {
        Assert.InRange(Math.Abs(DungeonObjectGeometry.Radius(category, typeRadius, sourceRadius, unitVolume) - expected), 0, 0.01);
    }

    [Theory]
    [InlineData(227, false)]
    [InlineData(1882, false)]
    [InlineData(1975, false)]
    [InlineData(226, true)]
    [InlineData(462, true)]
    public void DecorativeGroupsAreNonSolidWhileIndividualRocksRemainSolid(int groupId, bool expected)
    {
        Assert.Equal(expected, DungeonObjectGeometry.IsSolid(groupId));
    }
}
