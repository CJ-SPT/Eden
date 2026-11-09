using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Agents;

public sealed record MissionContainerInventory(InventoryItem Container, IReadOnlyList<InventoryItem> Items);

public interface IPilotMissionContainers
{
    MissionContainerInventory Read(long pilotId, long containerId);
    IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request);
}

public static class MissionContainerRules
{
    // Dedicated mission-space identity range, separate from scenery, NPCs and wrecks.
    public const long FirstId = 7_600_000_000_000;
    private const long EndId = 7_700_000_000_000;
    public const double TransferRange = 2_500;

    public static bool IsContainer(long id) => id >= FirstId && id < EndId;

    public static long Id(long missionId, int index) => checked(FirstId + missionId * MissionRules.NpcsPerMission + index);

    public static void Validate(SpacePosition ship, double shipRadius, StaticSpaceObject container, bool guarded)
    {
        if (guarded)
        {
            throw new InvalidDataException("Defeat the mission container's guards before opening it.");
        }
        if (ship.DistanceTo(container.Position) - shipRadius - container.Radius > TransferRange)
        {
            throw new InvalidDataException("Approach within 2,500 m of the mission container to open it.");
        }
    }
}
