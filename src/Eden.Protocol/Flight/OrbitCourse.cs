using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

// Adapted from Carbon Destiny 27ee40b, Ballpark::EvolveOldStyleOrbit.
// This is the reference's default orbit mode; see docs/carbon-license.md.
public static class OrbitCourse
{
    private const double PlanePrecession = 0.001;
    private const double DecimalPrecision = 10_000_000;
    private const double TransitionWidthSquared = 40_000;

    // The center radius includes the requested surface range and both radii.
    public static SpacePosition Direction(long shipId, int stamp, SpacePosition position, SpacePosition target, double centerRadius)
    {
        var offset = target - position;
        double distance = offset.Length;
        var toward = distance > 0 ? offset.Unit : new(0, 0, 1);
        double radius = centerRadius;
        double phase = stamp * PlanePrecession;
        double identityPhase = (shipId & ushort.MaxValue) + phase;
        SpacePosition normal = new(
            Truncate(Math.Cos(phase) * Math.Cos(identityPhase)),
            Truncate(Math.Sin(identityPhase)),
            Truncate(Math.Sin(phase) * Math.Cos(identityPhase))
        );
        var tangent = normal.Cross(toward).Unit;
        if (tangent == SpacePosition.Zero)
        {
            // Coincident or parallel vectors still need a finite escape direction.
            tangent = toward.Cross(Math.Abs(toward.Y) < 0.5 ? new(0, 1, 0) : new(1, 0, 0)).Unit;
        }
        double outside = distance * distance - radius * radius;
        if (outside >= 0 && distance > 0)
        {
            toward = (toward * (outside / distance) + tangent * (radius * Math.Sqrt(outside) / distance)).Unit;
        }
        double tangentialWeight = Truncate(Math.Exp(-Math.Pow(radius - distance, 2) / TransitionWidthSquared));
        double cosine = -toward.Dot(tangent);
        double discriminant = 1 + tangentialWeight * tangentialWeight * (cosine * cosine - 1);
        double radialWeight = tangentialWeight * cosine + Math.Sqrt(Math.Max(0, discriminant));
        radialWeight *= distance >= radius ? 1 : -1;
        return tangent * tangentialWeight + toward * radialWeight;
    }

    private static double Truncate(double value) => Math.Truncate(value * DecimalPrecision) / DecimalPrecision;
}
