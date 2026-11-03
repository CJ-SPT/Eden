namespace Eden.Gameplay.Flight;

public sealed record SpacePosition(double X, double Y, double Z)
{
    public static SpacePosition Zero { get; } = new(0, 0, 0);

    public double Length => DistanceTo(Zero);
    public SpacePosition Unit
    {
        get
        {
            double length = Length;
            return length > 0 ? this * (1 / length) : Zero;
        }
    }

    public double Dot(SpacePosition other) => X * other.X + Y * other.Y + Z * other.Z;

    public SpacePosition Cross(SpacePosition other) => new(Y * other.Z - Z * other.Y, Z * other.X - X * other.Z, X * other.Y - Y * other.X);

    public static SpacePosition operator +(SpacePosition a, SpacePosition b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static SpacePosition operator -(SpacePosition a, SpacePosition b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static SpacePosition operator *(SpacePosition a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

    public double DistanceTo(SpacePosition other)
    {
        double x = X - other.X,
            y = Y - other.Y,
            z = Z - other.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}
