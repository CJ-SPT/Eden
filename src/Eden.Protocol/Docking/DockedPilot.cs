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

public sealed record SpacePosition(double X, double Y, double Z);

public sealed record FlightProperties(float StationRadius, float ShipRadius, double Mass, float Agility, float MaxSpeed);

public sealed record StationIdentity(string Name, int GroupId, int CategoryId);

public sealed record HullProperties(double Structure, double Capacitor, double RechargeTime, double Shield, double Armor, double ShieldRechargeTime);
