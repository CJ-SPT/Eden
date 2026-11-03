using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Combat;

public static class MissileFlightRules
{
    // Carbon Destiny 114e89f: 1000 ms evolution ticks, 800 ms straight
    // launch phase, then EvolveMissile's velocity-led center interception.
    public const double TickSeconds = 1;
    public const double LaunchPhaseSeconds = .8;
    public const double CollisionTolerance = .0001;
    public const double InertiaMassScale = 1_000_000;
    private const int MaximumCollisionDepth = 60;
    private const double MinimumLaunchSpeed = 150;
    private const double LaunchGotoDistance = 1e16;
    private const double OrphanGotoDistance = 1e17;

    public static void Validate(MissileDrive drive)
    {
        if (
            !double.IsFinite(drive.Mass)
            || drive.Mass <= 0
            || !float.IsFinite(drive.Radius)
            || drive.Radius < 0
            || !float.IsFinite(drive.MaxVelocity)
            || drive.MaxVelocity <= 0
            || !float.IsFinite(drive.Agility)
            || drive.Agility <= 0
            || !double.IsFinite(drive.InertiaSeconds)
            || drive.InertiaSeconds <= 0
            || !double.IsFinite(drive.LifetimeMilliseconds)
            || drive.LifetimeMilliseconds <= 0
            || drive.LifetimeSeconds <= 0
        )
        {
            throw new InvalidDataException("Invalid missile drive attributes.");
        }
    }

    public static MissileFlight Launch(MissileDrive drive, MissileLaunchPlatform source, MissileTarget target, bool aimed)
    {
        Validate(drive);
        Validate(target);
        Validate(source.Position);
        Validate(source.Velocity);
        Validate(source.RearDirection);
        if (!float.IsFinite(source.MaxVelocity) || source.MaxVelocity < 0)
        {
            throw new InvalidDataException("Invalid missile launch platform speed.");
        }
        var direction = source.Velocity.Length > 0 ? source.Velocity.Unit : source.RearDirection.Unit;
        if (!aimed && direction.Length == 0)
        {
            throw new InvalidDataException("An unaimed launch requires a platform direction.");
        }
        // PyLaunchMissile uses a unit (1 m/s) velocity for aimed launches.
        // Unaimed launches inherit velocity plus max(150, platform maximum).
        var velocity = aimed
            ? (target.Position - source.Position).Unit
            : source.Velocity + direction * Math.Max(MinimumLaunchSpeed, source.MaxVelocity);
        return new(drive, source.Position, velocity, velocity.Unit * LaunchGotoDistance);
    }

    public static MissileFlight Tick(MissileFlight flight, MissileTarget? target)
    {
        if (flight.Status != MissileFlightStatus.Flying)
        {
            return flight;
        }
        Validate(flight);
        bool orphaned = flight.Orphaned || target is null;
        if (!orphaned)
        {
            Validate(target!);
        }
        var destination = flight.Goto;
        if (orphaned && !flight.Orphaned)
        {
            // Carbon StopAllFollowers changes a lost missile to GotoDirection;
            // it keeps flying without reacquiring until its lifetime expires.
            destination = flight.Position + flight.Velocity.Unit * OrphanGotoDistance;
        }
        else if (!orphaned && flight.ElapsedSeconds > LaunchPhaseSeconds)
        {
            double intercept = flight.Position.DistanceTo(target!.Position) / flight.Drive.MaxVelocity;
            destination = target.Position + target.Velocity * intercept;
        }
        Validate(destination);
        double duration = Math.Min(TickSeconds, flight.Drive.LifetimeSeconds - flight.ElapsedSeconds);
        var terminalVelocity = (destination - flight.Position).Unit * flight.Drive.MaxVelocity;
        // The missile overload of GotoThrust keeps full thrust near its target;
        // normal Goto instead tapers thrust with the fourth power of distance.
        if (orphaned || flight.ElapsedSeconds <= LaunchPhaseSeconds)
        {
            double distance = destination.DistanceTo(flight.Position);
            terminalVelocity *= Math.Min(1, Math.Pow(distance / (flight.Drive.MaxVelocity * TickSeconds), 4));
        }
        Trajectory trajectory = new(flight, terminalVelocity);
        double? impact = orphaned ? null : Contact(trajectory, target!, duration);
        double elapsed = impact ?? duration;
        var motion = trajectory.At(elapsed);
        Validate(motion.Position);
        Validate(motion.Velocity);
        double age = Math.Min(flight.ElapsedSeconds + elapsed, flight.Drive.LifetimeSeconds);
        return flight with
        {
            Position = motion.Position,
            Velocity = motion.Velocity,
            Goto = destination,
            ElapsedSeconds = age,
            Orphaned = orphaned,
            Status =
                impact.HasValue ? MissileFlightStatus.Impact
                : age >= flight.Drive.LifetimeSeconds ? MissileFlightStatus.Expired
                : MissileFlightStatus.Flying,
        };
    }

    public static void Validate(MissileFlight flight)
    {
        Validate(flight.Drive);
        Validate(flight.Position);
        Validate(flight.Velocity);
        Validate(flight.Goto);
        if (
            !double.IsFinite(flight.ElapsedSeconds)
            || flight.ElapsedSeconds < 0
            || flight.ElapsedSeconds > flight.Drive.LifetimeSeconds
            || flight.Status == MissileFlightStatus.Flying && flight.ElapsedSeconds == flight.Drive.LifetimeSeconds
            || !Enum.IsDefined(flight.Status)
        )
        {
            throw new InvalidDataException("Invalid missile evolution state.");
        }
    }

    private static void Validate(MissileTarget target)
    {
        Validate(target.Position);
        Validate(target.Velocity);
        if (!float.IsFinite(target.Radius) || target.Radius < 0)
        {
            throw new InvalidDataException("Invalid missile target radius.");
        }
    }

    private static void Validate(SpacePosition vector)
    {
        if (!double.IsFinite(vector.Length))
        {
            throw new InvalidDataException("Invalid missile flight geometry.");
        }
    }

    private sealed record Motion(SpacePosition Position, SpacePosition Velocity);

    private sealed record Trajectory(MissileFlight Flight, SpacePosition TerminalVelocity)
    {
        internal Motion At(double seconds)
        {
            double inertia = Flight.Drive.InertiaSeconds;
            double ratio = seconds / inertia;
            // Stable forms of Carbon Integrate's exponential drag solution.
            double change = ratio < .00001 ? ratio * (1 - ratio / 2 + ratio * ratio / 6) : 1 - Math.Exp(-ratio);
            var velocity = Flight.Velocity + (TerminalVelocity - Flight.Velocity) * change;
            var displacement =
                seconds >= inertia
                    ? TerminalVelocity * seconds + (Flight.Velocity - TerminalVelocity) * (inertia * change)
                    : Flight.Velocity * seconds
                        + (TerminalVelocity - Flight.Velocity)
                            * (ratio < .00001 ? seconds * ratio * (.5 - ratio / 6 + ratio * ratio / 24) : seconds - inertia * change);
            return new(Flight.Position + displacement, velocity);
        }

        internal double ChordDeviation(double start, double duration)
        {
            double inertia = Flight.Drive.InertiaSeconds;
            double change = (Flight.Velocity - TerminalVelocity).Length;
            // Bounds on the exponential curve's distance from its chord: its
            // remaining amplitude, or maximum acceleration * duration² / 8.
            double bound = inertia < duration / 8 ? change * inertia : change * (duration / inertia) * (duration / 8);
            return bound * Math.Exp(-start / inertia);
        }
    }

    private static double? Contact(Trajectory trajectory, MissileTarget target, double duration)
    {
        double radius = (double)trajectory.Flight.Drive.Radius + target.Radius;
        SpacePosition Relative(double time) => trajectory.At(time).Position - (target.Position + target.Velocity * time);
        return Search(0, duration, Relative(0), Relative(duration), 0);

        double? Search(double start, double end, SpacePosition first, SpacePosition last, int depth)
        {
            if (first.Length <= radius + CollisionTolerance)
            {
                return start;
            }
            double deviation = trajectory.ChordDeviation(start, end - start);
            double? intersection = SegmentEntry(first, last, radius + deviation + CollisionTolerance / 2);
            if (intersection is null)
            {
                return null;
            }
            if (deviation <= CollisionTolerance / 2)
            {
                return start + intersection.Value * (end - start);
            }
            if (depth >= MaximumCollisionDepth)
            {
                throw new InvalidDataException("Missile collision geometry exceeded the supported precision.");
            }
            double middle = (start + end) / 2;
            var point = Relative(middle);
            return Search(start, middle, first, point, depth + 1) ?? Search(middle, end, point, last, depth + 1);
        }
    }

    private static double? SegmentEntry(SpacePosition first, SpacePosition last, double radius)
    {
        if (first.Length <= radius)
        {
            return 0;
        }
        var delta = last - first;
        double lengthSquared = delta.Dot(delta);
        if (lengthSquared == 0)
        {
            return null;
        }
        double projection = -first.Dot(delta) / lengthSquared;
        if ((first + delta * Math.Clamp(projection, 0, 1)).Length > radius)
        {
            return null;
        }
        double perpendicular = (first + delta * projection).Length;
        double halfSpan = Math.Sqrt(Math.Max(0, (radius * radius - perpendicular * perpendicular) / lengthSquared));
        return Math.Clamp(projection - halfSpan, 0, 1);
    }
}
