using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;

namespace Eden.Gameplay.Docking;

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
    private const double LaunchClearance = 500;
    private const double LocalLaunchLaneSpacing = 1500;

    public static SpacePosition UndockPosition(long shipId, SpacePosition station, FlightProperties hull)
    {
        // Collision checks measure both radii. Large hulls must start outside
        // the station sphere, with the same clearance as smaller ships.
        double lane = shipId % 2 == 0 ? LocalLaunchLaneSpacing : 0;
        return new(station.X + hull.StationRadius + hull.ShipRadius + LaunchClearance, station.Y + lane, station.Z);
    }
}

public sealed record PilotIdentity(
    long Id,
    string Name,
    int CorporationId,
    AccountProfile Account,
    PilotProfile Profile,
    string CorporationTicker = ""
);

public sealed record PilotShip(
    long Id,
    int TypeId,
    int GroupId,
    int CategoryId,
    string Name,
    double CargoCapacity,
    int TurretSlots,
    FlightProperties Flight,
    HullProperties Hull
);

public sealed record PilotStation(
    int Id,
    int TypeId,
    int OwnerId,
    FlightSystem System,
    int OperationId,
    int OrbitId,
    SpacePosition Position,
    StationIdentity Identity
);

public sealed record PilotLocation(SpacePosition? Position = null, FlightSystem? System = null);

public sealed record DockedPilot(PilotIdentity Identity, PilotShip Ship, PilotStation Home, PilotLocation Location)
{
    public long PilotId => Identity.Id;
    public string Name
    {
        get => Identity.Name;
        init => Identity = Identity with { Name = value };
    }
    public int CorpId => Identity.CorporationId;
    public long ShipId
    {
        get => Ship.Id;
        init => Ship = Ship with { Id = value };
    }
    public int ShipTypeId => Ship.TypeId;
    public int ShipGroupId
    {
        get => Ship.GroupId;
        init => Ship = Ship with { GroupId = value };
    }
    public int ShipCategoryId => Ship.CategoryId;
    public int StationId => Home.Id;
    public int StationTypeId => Home.TypeId;
    public int StationOwnerId => Home.OwnerId;
    public int HomeSystemId => Home.System.Id;
    public int HomeConstellationId => Home.System.ConstellationId;
    public int HomeRegionId => Home.System.RegionId;
    public int OperationId => Home.OperationId;
    public int OrbitId => Home.OrbitId;
    public double X => Home.Position.X;
    public double Y => Home.Position.Y;
    public double Z => Home.Position.Z;
    public string ShipName => Ship.Name;
    public double CargoCapacity => Ship.CargoCapacity;
    public int TurretSlots => Ship.TurretSlots;
    public FlightProperties Flight
    {
        get => Ship.Flight;
        init => Ship = Ship with { Flight = value };
    }
    public StationIdentity Station => Home.Identity;
    public HullProperties Hull => Ship.Hull;
    public AccountProfile Account => Identity.Account;
    public PilotProfile Profile => Identity.Profile;
    public SpacePosition? Position
    {
        get => Location.Position;
        init => Location = Location with { Position = value };
    }
    public FlightSystem? CurrentSystem
    {
        get => Location.System;
        init => Location = Location with { System = value };
    }
    public string CorporationTicker
    {
        get => Identity.CorporationTicker;
        init => Identity = Identity with { CorporationTicker = value };
    }
    public int SystemId => CurrentSystem?.Id ?? HomeSystemId;
    public int ConstellationId => CurrentSystem?.ConstellationId ?? HomeConstellationId;
    public int RegionId => CurrentSystem?.RegionId ?? HomeRegionId;
    public int LocationId => Position is null ? StationId : SystemId;
}

// Pinned clonegrade.const CLONE_STATE_ALPHA/OMEGA values.
public enum AccountCloneGrade
{
    Alpha = 0,
    Omega = 1,
}

public sealed record AccountProfile(string UserName, long CreatedAt, AccountCloneGrade CloneGrade = AccountCloneGrade.Alpha);

public sealed record PilotProfile(int RaceId, int BloodlineId, int GenderId, int AncestryId, int SchoolId, long CreatedAt);

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
