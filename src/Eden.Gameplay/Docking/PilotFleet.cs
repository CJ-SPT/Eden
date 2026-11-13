using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Docking;

public sealed record ShipAssembly(IReadOnlyList<long> ShipIds, IReadOnlyList<InventoryChange> Changes);

public sealed record ShipBoarding(DockedPilot Pilot, IReadOnlyList<InventoryChange> Changes);

public static class FleetDefinitions
{
    // Build 3662168's native ship-name prompt permits 32 characters.
    public const int MaximumShipNameLength = 32;
    public const int CorvetteGroup = 237;
    public const int CapsuleGroup = 29;
    public const int CapsuleType = 670;
}

public interface IPilotFleet
{
    IReadOnlyList<InventoryItem> Ships(long pilotId, long stationId);
    InventoryItem? Find(long pilotId, long shipId);
    string Name(long pilotId, long shipId);
    string RenameDockedShip(long pilotId, long shipId, string name);
    ShipAssembly Assemble(long pilotId, IReadOnlyList<long> itemIds, string? name);
    ShipBoarding Leave(long pilotId, long expectedShipId);
    ShipBoarding Board(long pilotId, long shipId, long previousShipId);
    ShipBoarding RecoverCorvette(long pilotId, long previousShipId, long stationId);
}

public enum StarterShipType
{
    Venture = 32880,
}
