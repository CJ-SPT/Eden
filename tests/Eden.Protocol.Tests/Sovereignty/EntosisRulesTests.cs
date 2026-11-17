using Eden.Gameplay.Flight;
using Eden.Gameplay.Sovereignty;

namespace Eden.Protocol.Tests.Sovereignty;

public sealed class EntosisRulesTests
{
    [Fact]
    public void DenseFleetIntervalsDoNotStackAndAllocationGrowthIsBounded()
    {
        var fleet = Fleet(4096);
        var result = EntosisRules.Advance(new(null, 0, 0), fleet, 8192, 100000);
        Assert.Equal(6144, result.ProgressTicks);
        Assert.Equal(1, result.ScoringAllianceId);
        Assert.False(result.Blocked);
        _ = Allocations(128);
        long smaller = Allocations(512);
        long larger = Allocations(1024);
        Assert.True(larger < smaller * 3, $"Doubling links allocated {smaller} then {larger} bytes.");

        static EntosisContribution[] Fleet(int count) =>
            Enumerable
                .Range(0, count)
                .Select(index => new EntosisContribution(1, index, 100000))
                .Append(new(2, count / 2, count))
                .Reverse()
                .ToArray();

        static long Allocations(int count)
        {
            var links = Fleet(count);
            long before = GC.GetAllocatedBytesForCurrentThread();
            _ = EntosisRules.Advance(new(null, 0, 0), links, count * 2, 100000);
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
    }

    [Fact]
    public void ContestedLinksPauseWithoutStackingAndOpponentsReverseProgress()
    {
        var first = EntosisRules.Advance(new(null, 0, 0), [new(1, 0, 100), new(1, 0, 100)], 40, 100);
        Assert.Equal((1, 40L), (first.ControllingAllianceId, first.ProgressTicks));
        var contested = EntosisRules.Advance(first, [new(1, 0, 100), new(2, 0, 100)], 60, 100);
        Assert.Equal(40, contested.ProgressTicks);
        Assert.True(contested.Blocked);
        Assert.Equal(0, contested.Rate);
        var reversed = EntosisRules.Advance(contested, [new(2, 60, 300)], 110, 100);
        Assert.Equal((2, 10L), (reversed.ControllingAllianceId, reversed.ProgressTicks));
        var completed = EntosisRules.Advance(reversed, [new(2, 60, 300)], 250, 100);
        Assert.Equal(200, completed.CompletedAt);
        Assert.Equal(100, completed.ProgressTicks);
    }

    [Fact]
    public void WarmupExpiryAndLateCallbacksNeverScoreUnpaidTime()
    {
        var progress = EntosisRules.Advance(new(null, 0, 0), [new(1, 50, 100)], 1000, 200);
        Assert.Equal(50, progress.ProgressTicks);
        Assert.Null(progress.CompletedAt);
        Assert.Equal(0, progress.Rate);
        Assert.Equal(progress, EntosisRules.Advance(progress, [new(1, 50, 100)], 500, 200));
    }

    [Fact]
    public void PlacementUsesCatalogStarDistanceAndRejectsInvalidCoordinates()
    {
        Assert.Equal(new(2_000_000_100, 0, 0), EntosisRules.Placement(new(100, 0, 0), new(200, 0, 0), 2_000_000_000));
        Assert.Equal(new(0, 2_000_000_000, 0), EntosisRules.Placement(SpacePosition.Zero, SpacePosition.Zero, 2_000_000_000));
        Assert.Throws<InvalidDataException>(() => EntosisRules.Placement(SpacePosition.Zero, new(double.NaN, 0, 0), 1));
    }
}
