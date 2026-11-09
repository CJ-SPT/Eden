using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Flight;

public sealed class SiteWarpInRuleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(10000)]
    public void OverlappingObstaclesMoveLandingOnlyFarEnoughTowardTheApproach(double range)
    {
        SpacePosition origin = new(1e9, 0, 0);
        var landing = WarpCourse.LandingPoint(1, 500, origin, SpacePosition.Zero, range);
        StaticSpaceObject[] bodies =
        [
            new(2, 1, 226, 2, 0, landing, 1500),
            new(3, 1, 226, 2, 0, landing + new SpacePosition(2500, 0, 0), 1500),
        ];
        var anchor = SiteWarpInRules.ResolveAnchor(1, 500, origin, SpacePosition.Zero, range, bodies);
        var adjusted = WarpCourse.LandingPoint(1, 500, origin, anchor, range);
        Assert.Equal(4501, adjusted.X - landing.X, 6);
        Assert.All(bodies, body => Assert.True(adjusted.DistanceTo(body.Position) > body.Radius + 500));
    }

    [Fact]
    public void ClearOrDecorativeGeometryDoesNotMoveTheRequestedAnchor()
    {
        SpacePosition origin = new(1e9, 0, 0);
        StaticSpaceObject[] bodies =
        [
            new(2, 1, 1975, 2, 0, SpacePosition.Zero, 70000) { Solid = false },
            new(3, 1, 226, 2, 0, new(0, 10000, 0), 1500),
        ];
        Assert.Equal(SpacePosition.Zero, SiteWarpInRules.ResolveAnchor(1, 500, origin, SpacePosition.Zero, 0, bodies));
    }
}
