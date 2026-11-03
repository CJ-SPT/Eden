using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

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

public interface IStationDocking
{
    DockedPilot Dock(long pilotId, long shipId, int stationId);
}

public static class DockingDefinitions
{
    public const double MaximumRange = 2500;
}

public sealed record DockedPilot(
    long PilotId,
    string Name,
    int CorpId,
    long ShipId,
    int ShipTypeId,
    int ShipGroupId,
    int ShipCategoryId,
    int StationId,
    int StationTypeId,
    int StationOwnerId,
    int HomeSystemId,
    int HomeConstellationId,
    int HomeRegionId,
    int OperationId,
    int OrbitId,
    double X,
    double Y,
    double Z,
    string ShipName,
    double CargoCapacity,
    int TurretSlots,
    FlightProperties Flight,
    StationIdentity Station,
    HullProperties Hull,
    AccountProfile Account,
    PilotProfile Profile,
    SpacePosition? Position = null,
    FlightSystem? CurrentSystem = null
)
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
    public static Dictionary<DogmaAttributeId, int> TrainingAttributes() =>
        new()
        {
            [DogmaAttributeId.Charisma] = 19,
            [DogmaAttributeId.Intelligence] = 20,
            [DogmaAttributeId.Memory] = 20,
            [DogmaAttributeId.Perception] = 20,
            [DogmaAttributeId.Willpower] = 20,
        };
}

public sealed record FlightSystem(int Id, int ConstellationId, int RegionId);

public sealed record FlightProperties(float StationRadius, float ShipRadius, double Mass, float Agility, float MaxSpeed, double WarpSpeed)
{
    public double InertiaTime => Mass * Agility / 1_000_000;
}

public sealed record StationIdentity(string Name, int GroupId, int CategoryId);

public sealed record HullProperties(
    double Structure,
    double Capacitor,
    double RechargeTime,
    double Shield,
    double Armor,
    double ShieldRechargeTime
);
