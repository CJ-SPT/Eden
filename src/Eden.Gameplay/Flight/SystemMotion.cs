using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Flight;

public sealed record ShipMotion(
    long Id,
    int SystemId,
    int Stamp,
    SpacePosition Position,
    FlightProperties Hull,
    NavigationState Navigation,
    bool Solid = true,
    StaticSpaceObject? Target = null
);

public sealed record ShipMotionResult(ShipMotion Ship, IReadOnlyList<NavigationChange> Changes, bool Bumped = false);

public interface ISystemMotionStore
{
    IReadOnlyList<ShipMotionResult> Advance(
        int systemId,
        int stamp,
        long now,
        IReadOnlyList<ShipMotion> ships,
        IReadOnlyList<StaticSpaceObject> objects
    );
}

public sealed record CollisionMotion(
    long Id,
    FlightProperties Hull,
    SpacePosition Start,
    SpacePosition InitialVelocity,
    SpacePosition Position,
    SpacePosition Velocity,
    bool Solid = true,
    bool Fixed = false
);

public sealed record CollisionResult(long Id, SpacePosition Position, SpacePosition Velocity, bool Bumped);

public static class ShipCollisions
{
    // Carbon Destiny 27ee40b, Ballpark::Potential. Legacy collision acceleration is damped by 0.85.
    private const double ResponseScale = 0.85;
    private const double OverlapClearance = 1;
    private const double HeavyMassRatio = 25;
    private const double RelativeVelocitySquared = 0.0001;

    public static IReadOnlyList<CollisionResult> Resolve(IReadOnlyList<CollisionMotion> ships)
    {
        List<CollisionResult> results = [];
        var index = CollisionIndex.Create(ships);
        List<int> candidates = [];
        foreach (var ship in ships)
        {
            IEnumerable<CollisionMotion> others = ships;
            if (index is not null)
            {
                index.Find(ship, candidates);
                others = candidates.Select(candidate => ships[candidate]);
            }
            results.Add(ResolveOne(ship, others));
        }
        return results;
    }

    public static CollisionResult ResolveStatic(CollisionMotion ship, IReadOnlyList<StaticSpaceObject> obstacles) =>
        ResolveOne(
            ship,
            obstacles.Select(body => new CollisionMotion(
                body.Id,
                ship.Hull with
                {
                    ShipRadius = body.Radius,
                },
                body.Position,
                SpacePosition.Zero,
                body.Position,
                SpacePosition.Zero,
                Solid: body.Solid,
                Fixed: true
            ))
        );

    private static CollisionResult ResolveOne(CollisionMotion ship, IEnumerable<CollisionMotion> others)
    {
        if (ship.Fixed)
        {
            return new(ship.Id, ship.Position, SpacePosition.Zero, false);
        }
        double lastTime = -1;
        var correction = SpacePosition.Zero;
        foreach (var other in others)
        {
            if (ship.Id == other.Id || !ship.Solid || !other.Solid)
            {
                continue;
            }
            var impact = Potential(ship, other, 0);
            if (impact is null || impact.Time < lastTime)
            {
                continue;
            }
            var acceleration = impact.Acceleration * ResponseScale;
            if (impact.Time > lastTime || acceleration.Dot(acceleration) > correction.Dot(correction))
            {
                lastTime = impact.Time;
                correction = acceleration;
            }
        }
        double tau = ship.Hull.InertiaTime;
        double decay = Math.Exp(-1 / tau);
        return new(
            ship.Id,
            ship.Position + correction * (tau * (1 - tau * (1 - decay))),
            ship.Velocity + correction * (tau * (1 - decay)),
            lastTime >= 0
        );
    }

    private sealed record Impact(double Time, SpacePosition Acceleration);

    private static Impact? Potential(CollisionMotion ship, CollisionMotion other, int depth)
    {
        double radius = ship.Hull.ShipRadius + other.Hull.ShipRadius;
        double time = ContactTime(ship, other, radius);
        if (time < 0)
        {
            return null;
        }
        double tau = ship.Hull.InertiaTime;
        var desired = DesiredVelocity(ship);
        SpacePosition acceleration;
        if (time > 0)
        {
            var first = Sample(ship, desired, time);
            var second = Sample(other, DesiredVelocity(other), time);
            var normal = (second.Position - first.Position).Unit;
            double relative = (second.Velocity - first.Velocity).Dot(normal);
            double impulse = other.Fixed ? 2 * relative : 2 * other.Hull.Mass / (ship.Hull.Mass + other.Hull.Mass) * relative;
            double remainderDecay = Math.Exp(-(1 - time) / tau);
            // Potential solves for a full-tick acceleration that produces the post-impact end velocity.
            acceleration = normal * (impulse * remainderDecay / (tau * (1 - Math.Exp(-1 / tau))));
        }
        else
        {
            var radial = other.Start - ship.Start;
            double distance = radial.Length;
            var normal = distance > 0 ? radial.Unit : new SpacePosition(ship.Id > other.Id ? 1 : -1, 0, 0);
            double clearance = radius - distance + OverlapClearance;
            double otherTau = other.Hull.InertiaTime;
            double displacement = other.Fixed ? clearance : otherTau / (tau + otherTau) * clearance;
            if (!other.Fixed && depth < 2)
            {
                var offset = normal * displacement;
                var otherOffset = normal * (tau / (tau + otherTau) * clearance);
                var recursive = Potential(
                    ship with
                    {
                        Start = ship.Start - offset,
                        Position = ship.Position - offset,
                    },
                    other with
                    {
                        Start = other.Start + otherOffset,
                        Position = other.Position + otherOffset,
                    },
                    depth + 1
                );
                if (
                    (ship.InitialVelocity - other.InitialVelocity).Dot(ship.InitialVelocity - other.InitialVelocity)
                    > RelativeVelocitySquared
                )
                {
                    var response = recursive?.Acceleration ?? SpacePosition.Zero;
                    if (otherTau / tau > HeavyMassRatio)
                    {
                        response = response.Unit * (clearance / ResponseScale);
                    }
                    return new(time, response);
                }
            }
            acceleration = normal * (-displacement * (tau + 1) / tau) - ship.InitialVelocity - normal * (desired.Dot(normal) / tau);
        }
        return new(time, acceleration);
    }

    private static SpacePosition DesiredVelocity(CollisionMotion ship)
    {
        if (ship.Fixed)
        {
            return SpacePosition.Zero;
        }
        double decay = Math.Exp(-1 / ship.Hull.InertiaTime);
        return (ship.Velocity - ship.InitialVelocity * decay) * (1 / (1 - decay));
    }

    private static CollisionResult Sample(CollisionMotion ship, SpacePosition desired, double seconds)
    {
        double tau = ship.Hull.InertiaTime;
        double decay = Math.Exp(-seconds / tau);
        return new(
            ship.Id,
            ship.Start + desired * seconds + (ship.InitialVelocity - desired) * (tau * (1 - decay)),
            desired + (ship.InitialVelocity - desired) * decay,
            false
        );
    }

    private static double ContactTime(CollisionMotion ship, CollisionMotion other, double radius)
    {
        double radialX = ship.Start.X - other.Start.X;
        double radialY = ship.Start.Y - other.Start.Y;
        double radialZ = ship.Start.Z - other.Start.Z;
        double deltaX = (ship.Position.X - ship.Start.X) - (other.Position.X - other.Start.X);
        double deltaY = (ship.Position.Y - ship.Start.Y) - (other.Position.Y - other.Start.Y);
        double deltaZ = (ship.Position.Z - ship.Start.Z) - (other.Position.Z - other.Start.Z);
        double clearance = radialX * radialX + radialY * radialY + radialZ * radialZ - radius * radius;
        if (clearance <= 0)
        {
            return 0;
        }
        double speedSquared = deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ;
        double along = radialX * deltaX + radialY * deltaY + radialZ * deltaZ;
        double discriminant = along * along - speedSquared * clearance;
        if (speedSquared == 0 || along >= 0 || discriminant < 0)
        {
            return -1;
        }
        double time = clearance / (-along + Math.Sqrt(discriminant));
        return time <= 1 ? time : -1;
    }
}
