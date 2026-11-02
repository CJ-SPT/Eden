namespace Eden.Protocol.Docking;

public sealed record ShipAssembly(IReadOnlyList<long> ShipIds, IReadOnlyList<InventoryChange> Changes);
public sealed record ShipBoarding(DockedPilot Pilot, IReadOnlyList<InventoryChange> Changes);

public static class FleetDefinitions
{
    // Build 3662168's native ship-name prompt permits 32 characters.
    public const int MaximumShipNameLength = 32;
    public const int CorvetteGroup = 237;
}

public interface IPilotFleet
{
    IReadOnlyList<InventoryItem> Ships(long pilotId, int stationId);
    InventoryItem? Find(long pilotId, long shipId);
    string Name(long pilotId, long shipId);
    ShipAssembly Assemble(long pilotId, IReadOnlyList<long> itemIds, string? name);
    DockedPilot Board(long pilotId, long shipId, long previousShipId);
    ShipBoarding RecoverCorvette(long pilotId, long previousShipId, long stationId);
}

public enum StarterShipType
{
    Venture = 32880,
}
