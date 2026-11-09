using System.Collections.Immutable;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Agents;

public sealed class MissionSpawnRuleTests
{
    private static readonly ImmutableArray<MissionSpawnAnchor> Anchors =
    [
        new(1, new(0, 0, 20000), 30000, true, false),
        new(2, new(-60000, 0, 0), 70000, true, false),
        new(3, new(0, 50000, 0), 80000, false, true),
        new(4, new(0, 0, 50000), 90000, false, true),
        new(5, new(10000, 10000, 0), 40000, true, false, 100),
        new(6, new(10000, -10000, 0), 45000, true, false, 100),
    ];

    [Fact]
    public void InitialGroupsUseNearestReportedRangeAndNativeDirections()
    {
        var placements = MissionSpawnRules.Place(
            true,
            [new(10, 0, 18000, false), new(10, 0, 18000, false), new(20, 1, 55000, false)],
            Anchors
        );
        Assert.Equal(Anchors[0].Position, placements[0].Position);
        Assert.Equal(1500, placements[1].Position.DistanceTo(placements[0].Position));
        Assert.Equal(Anchors[1].Position, placements[2].Position);
        Assert.Equal(new double[] { 30000, 30000, 70000 }, placements.Select(p => p.AcquisitionRange));
    }

    [Fact]
    public void ReinforcementsUseTriggeredAnchorsAndDistributeEquallyDistantGroups()
    {
        MissionSpawnMember[] members = [new(10, 0, 50000, false), new(20, 1, 50000, false)];
        var placements = MissionSpawnRules.Place(false, members, Anchors);
        Assert.Equal(Anchors[2].Position, placements[0].Position);
        Assert.Equal(Anchors[3].Position, placements[1].Position);
        Assert.Equal(placements.ToArray(), MissionSpawnRules.Place(false, members, Anchors).ToArray());
    }

    [Fact]
    public void MultipleGroupsSharingOneAnchorDoNotOverlapMembers()
    {
        var placements = MissionSpawnRules.Place(true, [new(10, 0, 20000, false), new(20, 1, 20000, false)], [Anchors[0]]);
        Assert.Equal(1500, placements[0].Position.DistanceTo(placements[1].Position));
    }

    [Fact]
    public void MatchingBatteriesKeepIndividualFixedPositions()
    {
        var placements = MissionSpawnRules.Place(true, [new(100, 0, 55000, true), new(100, 0, 55000, true)], Anchors);
        Assert.Equal(new MissionSpawnPlacement(Anchors[4].Position, 40000), placements[0]);
        Assert.Equal(new MissionSpawnPlacement(Anchors[5].Position, 45000), placements[1]);
    }

    [Fact]
    public void MissingAnchorRetainsReportedDistanceAndDoesNotBorrowAnotherBatteryType()
    {
        var placement = Assert.Single(MissionSpawnRules.Place(true, [new(200, 0, 55000, true)], Anchors));
        Assert.Equal(new SpacePosition(55000, 0, 0), placement.Position);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidReportedRangesAreRejected(double range)
    {
        Assert.Throws<InvalidDataException>(() => MissionSpawnRules.Place(true, [new(10, 0, range, false)], Anchors));
    }
}
