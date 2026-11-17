namespace Eden.Gameplay.Flight;

public sealed record StaticCollisionImpact(double Time, SpacePosition Centre);

// A zero-length capsule is a sphere. Coordinates are relative to the owning rigid ball.
public sealed record StaticCollisionPrimitive(SpacePosition Start, SpacePosition End, float Radius)
{
    public bool Sphere => Start == End;

    public SpacePosition ClosestPoint(SpacePosition point)
    {
        var axis = End - Start;
        double lengthSquared = axis.Dot(axis);
        return lengthSquared == 0 ? Start : Start + axis * Math.Clamp((point - Start).Dot(axis) / lengthSquared, 0, 1);
    }

    public StaticCollisionImpact? Sweep(SpacePosition start, SpacePosition end, float shipRadius)
    {
        // Preserve the float radius sum used by the legacy sphere response.
        double radius = shipRadius + Radius;
        var centre = ClosestPoint(start);
        var offset = start - centre;
        if (offset.Dot(offset) <= radius * radius)
        {
            return new(0, centre);
        }
        var direction = end - start;
        Span<double> roots = stackalloc double[6];
        int count = Intersections(start, direction, radius, roots);
        double time = double.PositiveInfinity;
        for (int index = 0; index < count; index++)
        {
            double candidate = roots[index];
            if (candidate >= 0 && candidate <= 1 && candidate < time)
            {
                time = candidate;
            }
        }
        return double.IsFinite(time) ? new(time, ClosestPoint(start + direction * time)) : null;
    }

    internal double ExitDistance(SpacePosition start, SpacePosition direction, double padding)
    {
        Span<double> roots = stackalloc double[6];
        int count = Intersections(start, direction, Radius + padding, roots);
        double farthest = 0;
        for (int index = 0; index < count; index++)
        {
            farthest = Math.Max(farthest, roots[index]);
        }
        return farthest;
    }

    private int Intersections(SpacePosition start, SpacePosition direction, double radius, Span<double> roots)
    {
        int count = SphereRoots(start - Start, direction, radius, roots);
        if (Sphere)
        {
            return count;
        }
        count += SphereRoots(start - End, direction, radius, roots[count..]);
        var axis = End - Start;
        double lengthSquared = axis.Dot(axis);
        var offset = start - Start;
        double alongStart = offset.Dot(axis) / lengthSquared;
        double alongDirection = direction.Dot(axis) / lengthSquared;
        var perpendicularStart = offset - axis * alongStart;
        var perpendicularDirection = direction - axis * alongDirection;
        Span<double> cylinder = stackalloc double[2];
        int cylinderCount = SphereRoots(perpendicularStart, perpendicularDirection, radius, cylinder);
        for (int index = 0; index < cylinderCount; index++)
        {
            double along = alongStart + cylinder[index] * alongDirection;
            if (along is >= 0 and <= 1)
            {
                roots[count++] = cylinder[index];
            }
        }
        return count;
    }

    private static int SphereRoots(SpacePosition offset, SpacePosition direction, double radius, Span<double> roots)
    {
        double a = direction.Dot(direction);
        if (a == 0)
        {
            return 0;
        }
        double b = offset.Dot(direction);
        double c = offset.Dot(offset) - radius * radius;
        double discriminant = b * b - a * c;
        if (discriminant < 0)
        {
            return 0;
        }
        double squareRoot = Math.Sqrt(discriminant);
        roots[0] = (-b - squareRoot) / a;
        roots[1] = (-b + squareRoot) / a;
        return 2;
    }
}
