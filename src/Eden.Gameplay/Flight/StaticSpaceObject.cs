using Eden.Gameplay.Docking;

namespace Eden.Gameplay.Flight;

public sealed record StaticSpaceObject(
    long Id,
    int TypeId,
    int GroupId,
    int CategoryId,
    int OwnerId,
    SpacePosition Position,
    float Radius,
    int? DestinationGateId = null,
    int? DestinationSystemId = null
)
{
    public bool Solid { get; init; } = true;
}

public interface ISystemObjects
{
    FlightSystem GetSystem(int systemId);
    IReadOnlyList<StaticSpaceObject> Load(int systemId);
}
