using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Combat;

public sealed class MissileFlightTests
{
    private static MissileDrive Drive(double lifetime = 30000) => new(10000, 0, 300, .01f, lifetime);

    private static MissileLaunchPlatform Stationary => new(SpacePosition.Zero, SpacePosition.Zero, 0, new(0, 0, -1));
    private static MissileTarget Distant => new(new(1e6, 0, 0), SpacePosition.Zero, 1);

    [Theory]
    [InlineData(false, 0, 0, -150)]
    [InlineData(false, 10, 0, 160)]
    [InlineData(true, 0, 1, 0)]
    public void LaunchVelocitiesMatchCarbonsPublishedMissileCases(bool aimed, double platformSpeed, double x, double z)
    {
        var source = Stationary with { Velocity = new(0, 0, platformSpeed) };
        var flight = MissileFlightRules.Launch(Drive(), source, Distant, aimed);
        Assert.Equal(new(x, 0, z), flight.Velocity);
        Assert.Equal(SpacePosition.Zero, flight.Position);
        Assert.Equal(flight.Velocity.Unit * 1e16, flight.Goto);
        Assert.Equal(0, flight.ElapsedSeconds);
    }

    [Fact]
    public void UnaimedLaunchUsesThePlatformMaximumAndStationaryRearOrientation()
    {
        var source = Stationary with { MaxVelocity = 500, RearDirection = new(0, -2, 0) };
        Assert.Equal(new(0, -500, 0), MissileFlightRules.Launch(Drive(), source, Distant, false).Velocity);
        source = source with { Velocity = new(20, 0, 0) };
        Assert.Equal(new(520, 0, 0), MissileFlightRules.Launch(Drive(), source, Distant, false).Velocity);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 10)]
    [InlineData(true, 0)]
    public void TwentyTickCourseMatchesCarbonsPublicGuidanceAssertions(bool aimed, double platformSpeed)
    {
        var source = Stationary with { Velocity = new(0, 0, platformSpeed) };
        var flight = MissileFlightRules.Launch(Drive(), source, Distant, aimed);
        for (int tick = 0; tick < 20; tick++)
        {
            flight = MissileFlightRules.Tick(flight, Distant);
        }
        Assert.Equal(MissileFlightStatus.Flying, flight.Status);
        Assert.Equal(300, flight.Velocity.X, 2);
        Assert.InRange(Math.Abs(flight.Velocity.Y), 0, .5);
        Assert.InRange(Math.Abs(flight.Velocity.Z), 0, .5);
        Assert.True(flight.Position.X > 2000);
    }

    [Fact]
    public void InitialStraightTickPrecedesVelocityLedCenterGuidance()
    {
        var target = new MissileTarget(new(10000, 0, 0), new(0, 100, 0), 10);
        var flight = MissileFlightRules.Launch(Drive(), Stationary, target, false);
        var initial = MissileFlightRules.Tick(flight, target);
        Assert.Equal(flight.Goto, initial.Goto);
        Assert.Equal(0, initial.Velocity.X);
        Assert.True(initial.Velocity.Z < 0);
        target = target with { Position = target.Position + target.Velocity };
        var guided = MissileFlightRules.Tick(initial, target);
        Assert.Equal(target.Position.X, guided.Goto.X, 8);
        Assert.Equal(target.Position.Y + initial.Position.DistanceTo(target.Position) / 300 * 100, guided.Goto.Y, 8);
        Assert.Equal(0, guided.Goto.Z);
        Assert.True(guided.Velocity.X > 0);
        Assert.True(guided.Velocity.Y > 0);
    }

    private static MissileFlight Cruising(double speed = 300, double lifetime = 10000)
    {
        var drive = Drive(lifetime) with { MaxVelocity = (float)speed };
        var platform = Stationary with { Velocity = new(speed / 2, 0, 0), MaxVelocity = (float)(speed / 2) };
        return MissileFlightRules.Launch(drive, platform, Distant, false);
    }

    [Fact]
    public void MovingTargetIsHitAtItsActualInterceptRatherThanItsLaunchPosition()
    {
        var flight = Cruising();
        var target = new MissileTarget(new(1000, 0, 0), new(100, 0, 0), 10);
        while (flight.Status == MissileFlightStatus.Flying)
        {
            var current = target with { Position = target.Position + target.Velocity * flight.ElapsedSeconds };
            flight = MissileFlightRules.Tick(flight, current);
        }
        Assert.Equal(MissileFlightStatus.Impact, flight.Status);
        Assert.InRange(flight.ElapsedSeconds, 4.949999, 4.950001);
        Assert.InRange(flight.Position.X, 1484.999, 1485.001);
    }

    [Fact]
    public void FastMissileCannotTunnelThroughTargetBetweenTickEndpoints()
    {
        var flight = MissileFlightRules.Tick(Cruising(3000), new(new(1000, 0, 0), SpacePosition.Zero, 1));
        Assert.Equal(MissileFlightStatus.Impact, flight.Status);
        Assert.InRange(flight.ElapsedSeconds, .3329999, .3330001);
        Assert.InRange(flight.Position.X, 998.9998, 999.0001);
    }

    [Fact]
    public void CrossingTargetCanBeHitEvenWhenBothEndpointsAreOutsideTheMissilePath()
    {
        var target = new MissileTarget(new(500, -1000, 0), new(0, 2000, 0), 5);
        var flight = MissileFlightRules.Tick(Cruising(1000), target);
        Assert.Equal(MissileFlightStatus.Impact, flight.Status);
        Assert.InRange(flight.ElapsedSeconds, .4977638, .4977641);
        Assert.InRange(flight.Position.DistanceTo(target.Position + target.Velocity * flight.ElapsedSeconds), 4.9999, 5.0001);
    }

    [Fact]
    public void FasterTargetCanOutrunARealProjectileUntilItsLifetimeExpires()
    {
        var flight = Cruising(lifetime: 2250);
        for (int tick = 0; tick < 3; tick++)
        {
            flight = MissileFlightRules.Tick(flight, new(new(1000 + 350 * tick, 0, 0), new(350, 0, 0), 1));
        }
        Assert.Equal(MissileFlightStatus.Expired, flight.Status);
        Assert.Equal(2.25, flight.ElapsedSeconds);
        Assert.Equal(675, flight.Position.X, 8);
        Assert.Same(flight, MissileFlightRules.Tick(flight, Distant));
    }

    [Theory]
    [InlineData(299.9, MissileFlightStatus.Impact)]
    [InlineData(300.1, MissileFlightStatus.Expired)]
    public void FractionalFinalTickCannotTravelBeyondTheLifetime(double distance, MissileFlightStatus status)
    {
        var flight = MissileFlightRules.Tick(Cruising(lifetime: 1000), new(new(distance, 0, 0), SpacePosition.Zero, 0));
        Assert.Equal(status, flight.Status);
        Assert.True(flight.ElapsedSeconds <= 1);
    }

    [Fact]
    public void LostTargetCoastsWithoutReacquiringOrExplodingBeforeTimeout()
    {
        var flight = MissileFlightRules.Tick(Cruising(lifetime: 2250), null);
        Assert.True(flight.Orphaned);
        Assert.Equal(MissileFlightStatus.Flying, flight.Status);
        flight = MissileFlightRules.Tick(flight, new(new(301, 0, 0), SpacePosition.Zero, 100));
        Assert.Equal(MissileFlightStatus.Flying, flight.Status);
        Assert.Equal(600, flight.Position.X, 8);
        flight = MissileFlightRules.Tick(flight, null);
        Assert.Equal(MissileFlightStatus.Expired, flight.Status);
        Assert.Equal(675, flight.Position.X, 8);
    }

    private static MissileFlight Curving() => new(new(1000000, 0, 300, 1, 10000), SpacePosition.Zero, new(0, 300, 0), new(1e16, 0, 0));

    [Fact]
    public void ChordCrossingAloneDoesNotFalselyHitATargetOutsideTheCurvedPath()
    {
        // Midpoint of the full-tick chord, well inside the actual curved arc.
        var target = new MissileTarget(new(55.1819161757, 94.8180838243, 0), SpacePosition.Zero, 1);
        var flight = MissileFlightRules.Tick(Curving(), target);
        Assert.Equal(MissileFlightStatus.Flying, flight.Status);
        Assert.Equal(110.3638323514, flight.Position.X, 7);
        Assert.Equal(189.6361676486, flight.Position.Y, 7);
    }

    [Fact]
    public void AdaptiveSweepFindsASmallTargetOnTheCurvedPath()
    {
        var target = new MissileTarget(new(31.9591979138, 118.0408020862, 0), SpacePosition.Zero, .001f);
        var flight = MissileFlightRules.Tick(Curving(), target);
        Assert.Equal(MissileFlightStatus.Impact, flight.Status);
        Assert.InRange(flight.ElapsedSeconds, .49999, .50001);
        Assert.InRange(flight.Position.DistanceTo(target.Position), 0, .00111);
    }

    [Fact]
    public void LargeInertiaRetainsVelocityWithoutCancellationOrInstantAcceleration()
    {
        var flight = Cruising() with { Drive = new(1e12, 0, 300, 1, 10000), Velocity = new(10, 0, 0) };
        flight = MissileFlightRules.Tick(flight, Distant);
        Assert.InRange(flight.Position.X, 10.0001449, 10.0001451);
        Assert.InRange(flight.Velocity.X, 10.0002899, 10.0002901);
    }

    [Fact]
    public void AccelerationAndRelativeCollisionWorkAwayFromTheCoordinateOrigin()
    {
        var offset = new SpacePosition(1e12, -1e12, 5e11);
        var flight = Cruising() with { Position = offset, Goto = offset + new SpacePosition(1e16, 0, 0) };
        flight = MissileFlightRules.Tick(flight, new(offset + new SpacePosition(100, 0, 0), SpacePosition.Zero, 5));
        Assert.Equal(MissileFlightStatus.Impact, flight.Status);
        Assert.InRange(flight.Position.DistanceTo(offset), 94.999, 95.001);
    }

    [Theory]
    [InlineData("mass")]
    [InlineData("agility")]
    [InlineData("speed")]
    [InlineData("radius")]
    [InlineData("lifetime")]
    [InlineData("position")]
    [InlineData("time")]
    public void InvalidDriveOrStateCannotEvolveIntoNonfiniteCoordinates(string field)
    {
        var flight = Cruising();
        flight = field switch
        {
            "mass" => flight with { Drive = flight.Drive with { Mass = 0 } },
            "agility" => flight with { Drive = flight.Drive with { Agility = float.NaN } },
            "speed" => flight with { Drive = flight.Drive with { MaxVelocity = float.PositiveInfinity } },
            "radius" => flight with { Drive = flight.Drive with { Radius = -1 } },
            "lifetime" => flight with { Drive = flight.Drive with { LifetimeMilliseconds = 0 } },
            "position" => flight with { Position = new(double.NaN, 0, 0) },
            _ => flight with { ElapsedSeconds = -1 },
        };
        Assert.Throws<InvalidDataException>(() => MissileFlightRules.Tick(flight, Distant));
    }
}
