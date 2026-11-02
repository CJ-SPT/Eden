using Eden.Protocol.Docking;

namespace Eden.Protocol.Flight;

public sealed record StaticSpaceObject(int Id, int TypeId, int GroupId, int CategoryId, int OwnerId,
    SpacePosition Position, float Radius, int? DestinationGateId = null, int? DestinationSystemId = null);

public interface ISystemObjects
{
    FlightSystem GetSystem(int systemId);
    IReadOnlyList<StaticSpaceObject> Load(int systemId);
}
