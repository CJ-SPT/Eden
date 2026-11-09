using System.Collections.Immutable;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Exploration;

public sealed class AnomalySpawnRuleTests
{
    private static readonly AnomalySpawnPlacement Fallback = new(new(-50000, 0, 0), 90000);
    private static readonly ImmutableArray<AnomalySpawnAnchor> Anchors =
    [
        new(1, new(10000, 0, 0), 20000, true, false),
        new(2, new(20000, 0, 0), 30000, true, false),
        new(3, new(0, 0, 30000), 40000, false, true),
        new(4, new(0, 0, 40000), 50000, false, true),
        new(5, new(0, 10000, 0), 60000, true, false, 100),
        new(6, new(0, -10000, 0), 70000, true, false, 100),
    ];

    [Fact]
    public void InitialGroupsUseSeparateInitialAnchorsAndTheirAggressionRanges()
    {
        AnomalySpawnMember[] members = [new(10, 0, false, false), new(10, 0, false, false), new(20, 1, true, false)];
        var placements = AnomalySpawnRules.Place(0, members, Anchors, Fallback);
        Assert.Equal(Anchors[0].Position, placements[0].Position);
        Assert.Equal(1500, placements[0].Position.DistanceTo(placements[1].Position));
        Assert.Equal(Anchors[1].Position, placements[2].Position);
        Assert.Equal(new double[] { 20000, 20000, 30000 }, placements.Select(p => p.AcquisitionRange));
    }

    [Fact]
    public void ReinforcementsUseTriggeredAnchorsAndRepeatDeterministically()
    {
        AnomalySpawnMember[] members = [new(10, 0, false, false), new(20, 1, false, false)];
        var first = AnomalySpawnRules.Place(1, members, Anchors, Fallback);
        Assert.Equal(Anchors[2].Position, first[0].Position);
        Assert.Equal(Anchors[3].Position, first[1].Position);
        Assert.Equal(first.ToArray(), AnomalySpawnRules.Place(1, members, Anchors, Fallback).ToArray());
        var second = AnomalySpawnRules.Place(2, members, Anchors, Fallback);
        Assert.Equal(Anchors[3].Position, second[0].Position);
        Assert.Equal(Anchors[2].Position, second[1].Position);
    }

    [Fact]
    public void MatchingSentriesUseDistinctFixedPositionsWithoutFormationOffsets()
    {
        AnomalySpawnMember[] members = [new(100, 0, false, true), new(100, 0, false, true), new(10, 1, false, false)];
        var placements = AnomalySpawnRules.Place(0, members, Anchors, Fallback);
        Assert.Equal(new AnomalySpawnPlacement(Anchors[4].Position, 60000), placements[0]);
        Assert.Equal(new AnomalySpawnPlacement(Anchors[5].Position, 70000), placements[1]);
        Assert.Equal(Anchors[1].Position, placements[2].Position);
    }

    [Fact]
    public void MissingStageMetadataUsesMobileAnchorsWithoutUsingSentryPositions()
    {
        var anchors = Anchors.Select(a => a with { Initial = false, Reinforcement = false }).ToImmutableArray();
        var placements = AnomalySpawnRules.Place(0, [new(10, 0, false, false)], anchors, Fallback);
        Assert.Equal(anchors[0].Position, Assert.Single(placements).Position);
    }

    [Fact]
    public void ReinforcementSentriesDoNotReuseInitialTowerPositions()
    {
        var deferred = new AnomalySpawnAnchor(7, new(0, 20000, 0), 80000, false, true, 100);
        var placements = AnomalySpawnRules.Place(1, [new(100, 0, false, true)], Anchors.Add(deferred), Fallback);
        Assert.Equal(new AnomalySpawnPlacement(deferred.Position, deferred.AcquisitionRange), Assert.Single(placements));
    }

    [Fact]
    public void MissingOrExhaustedSentryAnchorsUseFallbackWithoutDuplicatingFixedTowers()
    {
        AnomalySpawnMember[] members = [new(100, 0, false, true), new(100, 0, false, true), new(100, 0, false, true)];
        var placements = AnomalySpawnRules.Place(0, members, Anchors, Fallback);
        Assert.Equal(3, placements.Select(p => p.Position).Distinct().Count());
        Assert.Equal(Fallback.AcquisitionRange, placements[2].AcquisitionRange);
        Assert.InRange(placements[2].Position.DistanceTo(Fallback.Position), 0, 1501);
        Assert.Equal(Fallback, Assert.Single(AnomalySpawnRules.Place(0, [new(200, 0, false, true)], [], Fallback)));
    }

    [Fact]
    public void NegativeWaveIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AnomalySpawnRules.Place(-1, [], Anchors, Fallback));
    }
}
