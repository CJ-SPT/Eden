using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Flight;

public sealed class NavigationState
{
    public SpacePosition LastHeading { get; private set; } = new(1, 0, 0);
    public SpacePosition? Direction { get; private set; }
    public SpacePosition Velocity { get; private set; } = new(0, 0, 0);
    public StaticSpaceObject? FollowTarget { get; private set; }
    public NavigationMode FollowMode { get; private set; } = NavigationMode.Follow;
    public double FollowRange { get; private set; }
    public SpacePosition? WarpDestination { get; private set; }
    public (long Target, double Range)? WarpOrder { get; private set; }
    public int AlignStamp { get; private set; }
    public WarpCourse? Warp { get; private set; }
    public double SpeedFraction { get; private set; }

    public void AdoptHull()
    {
        SetAtRest();
        Warp = null;
        WarpDestination = null;
        WarpOrder = null;
        AlignStamp = 0;
    }

    public void Dock()
    {
        AdoptHull();
        SpeedFraction = 1;
        LastHeading = new(1, 0, 0);
    }

    public void CancelAlignment() => WarpDestination = null;

    public bool StartWarp(long shipId, FlightProperties hull, SpacePosition origin, StaticSpaceObject body, double range, int stamp)
    {
        if (!double.IsFinite(range) || range is < 0 or > 100_000)
        {
            return false;
        }
        var anchor = CelestialWarpPoint.Resolve(body, origin);
        if (origin.DistanceTo(anchor) - hull.ShipRadius < 150_000)
        {
            return false;
        }
        var toward = (anchor - origin).Unit;
        double standOff = range + hull.ShipRadius + (shipId % 2 == 0 ? 1500 : 500);
        var end = anchor - toward * standOff;
        if (origin.DistanceTo(end) < 101_000)
        {
            return false;
        }
        WarpDestination = end;
        WarpOrder = (body.Id, range);
        AlignStamp = stamp;
        Direction = toward;
        LastHeading = Direction;
        SpeedFraction = 1;
        FollowTarget = null;
        return true;
    }

    public bool SetHeading(double x, double y, double z)
    {
        double length = Math.Sqrt(x * x + y * y + z * z);
        if (!(length > 0) || !double.IsFinite(length))
        {
            return false;
        }
        FollowTarget = null;
        LastHeading = new(x / length, y / length, z / length);
        Direction = LastHeading;
        if (SpeedFraction == 0)
        {
            SpeedFraction = 1;
        }
        return true;
    }

    public NavigationOrderResult Follow(
        long shipId,
        int stamp,
        SpacePosition position,
        float shipRadius,
        StaticSpaceObject follow,
        double standOff,
        bool orbit,
        bool inBallpark
    )
    {
        if (standOff < 0 || standOff > NavigationDefinitions.MaximumFollowRange || !double.IsFinite(standOff) || orbit && !inBallpark)
        {
            return NavigationOrderResult.Rejected;
        }

        double separation = position.DistanceTo(follow.Position);
        if (separation > 0 || orbit)
        {
            double radius = follow.Radius + shipRadius + standOff;
            double sign = separation >= radius ? 1 : -1;
            FollowMode = orbit ? NavigationMode.Orbit : NavigationMode.Follow;
            Direction =
                FollowMode == NavigationMode.Orbit
                    ? OrbitCourse.Direction(shipId, stamp, position, follow.Position, radius)
                    : (follow.Position - position) * (sign / separation);
            LastHeading = Direction;
            FollowTarget = follow;
            FollowRange = standOff;
            SpeedFraction = 1;
        }
        return separation > 0 || orbit ? NavigationOrderResult.Applied : NavigationOrderResult.Unchanged;
    }

    public bool SetSpeed(double fraction)
    {
        if (!(fraction >= 0 && fraction <= 1))
        {
            return false;
        }
        if (Direction is null && fraction > 0)
        {
            Direction = LastHeading;
            FollowTarget = null;
        }
        SpeedFraction = fraction;
        return true;
    }

    public void Stop()
    {
        Direction = null;
        FollowTarget = null;
        SpeedFraction = 0;
    }

    public NavigationTick Advance(
        long shipId,
        int stamp,
        FlightProperties hull,
        SpacePosition position,
        Func<long, StaticSpaceObject?> findTarget,
        IReadOnlyList<StaticSpaceObject> obstacles
    )
    {
        List<NavigationChange> changes = [];
        if (WarpDestination is { } destination)
        {
            Direction = (destination - position).Unit;
            double speed = Velocity.Length;
            if ((speed > hull.MaxSpeed * 0.75 && Velocity.Unit.Dot(Direction) > 0.99) || stamp - AlignStamp > 180)
            {
                Warp = new(position, destination, stamp, hull, speed);
                WarpDestination = null;
                Direction = null;
            }
        }
        bool justExited = false;
        if (Warp is { } warp)
        {
            var sample = warp.Sample(stamp, Math.Min(hull.MaxSpeed / 2, 100));
            position = sample.Position;
            Velocity = sample.Velocity;
            if (!sample.Exited)
            {
                return new(position, changes);
            }
            Warp = null;
            SpeedFraction = 0;
            justExited = true;
            changes.Add(new NavigationChange.WarpExited());
        }

        double cruise = hull.MaxSpeed * SpeedFraction;
        if (FollowTarget is { } follow)
        {
            if (findTarget(follow.Id) is not { } current)
            {
                // Carbon's interactive orbiters keep their heading when a target
                // leaves. Keep-at-range followers instead coast to a stop.
                FollowTarget = null;
                if (FollowMode == NavigationMode.Orbit && Velocity.Length > 0)
                {
                    Direction = LastHeading = Velocity.Unit;
                    changes.Add(new NavigationChange.Heading(Direction));
                }
                else
                {
                    Direction = null;
                    SpeedFraction = cruise = 0;
                    changes.Add(new NavigationChange.Stopped());
                }
            }
            else
            {
                FollowTarget = current;
                if (FollowMode == NavigationMode.Orbit)
                {
                    // Destiny evolves behavior at the beginning of each tick.
                    Direction = OrbitCourse.Direction(
                        shipId,
                        stamp - 1,
                        position,
                        current.Position,
                        hull.ShipRadius + current.Radius + FollowRange
                    );
                }
                else
                {
                    var radial = position - current.Position;
                    double error = radial.Length - current.Radius - hull.ShipRadius - FollowRange;
                    Direction = radial.Unit * -Math.Sign(error);
                    if (cruise > 0 && Math.Abs(error) < cruise)
                    {
                        // Destiny GotoThrust tapers within one tick's travel distance.
                        cruise *= Math.Pow(error / cruise, 4);
                    }
                }
            }
        }
        // Build 3662168 accepts the legacy free-ball layout (no orientation fields).
        // EvolveStop damps Y before drag on each STOP tick, except the warp-exit integration.
        if (Direction is null && !justExited)
        {
            Velocity = Velocity with { Y = Velocity.Y * (0.93 / 1.07) };
        }
        double tau = hull.InertiaTime;
        double decay = Math.Exp(-1 / tau);
        var targetVelocity = (Direction ?? SpacePosition.Zero) * cruise;
        var next = position + (targetVelocity + (Velocity - targetVelocity) * (tau * (1 - decay)));
        Velocity = targetVelocity + (Velocity - targetVelocity) * decay;
        if (FirstContact(position, next, hull.ShipRadius, obstacles) is { } contact)
        {
            next = contact;
            Velocity = SpacePosition.Zero;
            if (WarpDestination is null)
            {
                changes.Add(new NavigationChange.Collision(contact));
                SetAtRest();
            }
        }
        else if ((Direction is null || SpeedFraction == 0) && Velocity.Length < 0.01)
        {
            SetAtRest();
        }
        return new(next, changes);
    }

    public void SetAtRest()
    {
        Direction = null;
        FollowTarget = null;
        Velocity = new(0, 0, 0);
        SpeedFraction = 0;
    }

    private static SpacePosition? FirstContact(
        SpacePosition start,
        SpacePosition end,
        float shipRadius,
        IReadOnlyList<StaticSpaceObject> obstacles
    )
    {
        var delta = end - start;
        double lengthSquared = delta.Dot(delta);
        double earliest = double.PositiveInfinity;
        foreach (var body in obstacles)
        {
            var radial = start - body.Position;
            double along = radial.Dot(delta);
            if (along >= 0)
            {
                continue;
            }
            double radius = body.Radius + shipRadius;
            double clearance = Math.Max(0, radial.Dot(radial) - radius * radius);
            double discriminant = along * along - lengthSquared * clearance;
            if (discriminant > 0)
            {
                earliest = Math.Min(earliest, clearance / (-along + Math.Sqrt(discriminant)));
            }
        }
        return earliest <= 1 ? start + delta * earliest : null;
    }
}

public sealed record NavigationTick(SpacePosition Position, IReadOnlyList<NavigationChange> Changes);

public abstract record NavigationChange
{
    public sealed record WarpExited : NavigationChange;

    public sealed record Heading(SpacePosition Direction) : NavigationChange;

    public sealed record Stopped : NavigationChange;

    public sealed record Collision(SpacePosition Position) : NavigationChange;
}

public enum NavigationOrderResult
{
    Rejected,
    Unchanged,
    Applied,
}
