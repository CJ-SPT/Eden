namespace Eden.Protocol.Docking;

public interface IDockedWorld
{
    DockedPilot? FindPilot(long accountId);
}

public sealed record DockedPilot(long PilotId, string Name, int CorpId, long ShipId, int ShipTypeId,
    int ShipGroupId, int ShipCategoryId, int StationId, int StationTypeId, int StationOwnerId,
    int SystemId, int ConstellationId, int RegionId, int OperationId, int OrbitId, double X, double Y, double Z, string ShipName, double CargoCapacity, int TurretSlots);
