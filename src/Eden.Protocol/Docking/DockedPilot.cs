namespace Eden.Protocol.Docking;

public interface IPilotLookup
{
    DockedPilot? FindPilot(long accountId);
}

public interface IShipPositionStore
{
    void SetPosition(long shipId, SpacePosition? position);
}

public interface IDockedWorld : IPilotLookup, IShipPositionStore;

public sealed record DockedPilot(long PilotId, string Name, int CorpId, long ShipId, int ShipTypeId,
    int ShipGroupId, int ShipCategoryId, int StationId, int StationTypeId, int StationOwnerId,
    int SystemId, int ConstellationId, int RegionId, int OperationId, int OrbitId, double X, double Y, double Z, string ShipName, double CargoCapacity, int TurretSlots, FlightProperties Flight,
    StationIdentity Station, HullProperties Hull, SpacePosition? Position = null)
{
    public int LocationId => Position is null ? StationId : SystemId;
}

public sealed record SpacePosition(double X, double Y, double Z)
{
    public static SpacePosition Zero { get; } = new(0, 0, 0);

    internal double Length => DistanceTo(Zero);
    internal SpacePosition Unit
    {
        get
        {
            double length = Length;
            return length > 0 ? this * (1 / length) : Zero;
        }
    }
    internal double Dot(SpacePosition other) => X * other.X + Y * other.Y + Z * other.Z;
    public static SpacePosition operator +(SpacePosition a, SpacePosition b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static SpacePosition operator -(SpacePosition a, SpacePosition b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static SpacePosition operator *(SpacePosition a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);

    public double DistanceTo(SpacePosition other)
    {
        double x = X - other.X, y = Y - other.Y, z = Z - other.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }
}

public sealed record FlightProperties(float StationRadius, float ShipRadius, double Mass, float Agility, float MaxSpeed, double WarpSpeed)
{
    public double InertiaTime => Mass * Agility / 1_000_000;
}

public sealed record StationIdentity(string Name, int GroupId, int CategoryId);

public sealed record HullProperties(double Structure, double Capacitor, double RechargeTime, double Shield, double Armor, double ShieldRechargeTime);
