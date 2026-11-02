namespace Eden.Protocol.Docking;

public interface IPilotLookup
{
    AccountProfile ReadAccount(long accountId);
    IReadOnlyList<DockedPilot> ListPilots(long accountId);
}

public interface IShipPositionStore
{
    void SetPosition(long shipId, SpacePosition? position, int? systemId = null);
}

public interface IDockedWorld : IPilotLookup, IShipPositionStore;

public sealed record DockedPilot(long PilotId, string Name, int CorpId, long ShipId, int ShipTypeId,
    int ShipGroupId, int ShipCategoryId, int StationId, int StationTypeId, int StationOwnerId,
    int HomeSystemId, int HomeConstellationId, int HomeRegionId, int OperationId, int OrbitId, double X, double Y, double Z, string ShipName, double CargoCapacity, int TurretSlots, FlightProperties Flight,
    StationIdentity Station, HullProperties Hull, AccountProfile Account, PilotProfile Profile, SpacePosition? Position = null, FlightSystem? CurrentSystem = null)
{
    public string CorporationTicker { get; init; } = "";
    public int SystemId => CurrentSystem?.Id ?? HomeSystemId;
    public int ConstellationId => CurrentSystem?.ConstellationId ?? HomeConstellationId;
    public int RegionId => CurrentSystem?.RegionId ?? HomeRegionId;
    public int LocationId => Position is null ? StationId : SystemId;
}

public sealed record AccountProfile(string UserName, long CreatedAt);

public sealed record PilotProfile(int RaceId, int BloodlineId, int GenderId, int AncestryId, int SchoolId, long CreatedAt)
{
    // The standard neural map puts 19 points in charisma and 20 in each other attribute.
    public static Dictionary<long, object?> TrainingAttributes() => new()
    {
        [164] = 19,
        [165] = 20,
        [166] = 20,
        [167] = 20,
        [168] = 20,
    };
}

public sealed record FlightSystem(int Id, int ConstellationId, int RegionId);

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
