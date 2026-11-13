using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;

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
    public Eden.Gameplay.Exploration.WormholeContact? Wormhole { get; init; }
    public int? HackingState { get; init; }
    public bool Solid { get; init; } = true;
    public bool IsGlobal { get; init; } = true;
    public string? Name { get; init; }
    public NpcStandingThresholds? StandingThresholds { get; init; }
    public SpaceContainerState? Container { get; init; }
    public Structures.StructureContact? Structure { get; init; }
}

public interface ISystemObjects
{
    FlightSystem GetSystem(int systemId);
    IReadOnlyList<StaticSpaceObject> Load(int systemId);
}
