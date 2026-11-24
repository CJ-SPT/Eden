using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Exploration;

public interface IIncursionCargo
{
    MissionContainerInventory Read(long pilotId, long containerId);
    IReadOnlyList<InventoryChange> Take(long pilotId, long shipId, InventoryMoveRequest request);
    IReadOnlyList<InventoryChange> Deposit(long pilotId, long shipId, long containerId, InventoryMoveRequest request);
}

public static class IncursionObjectIds
{
    // The last slots of each combat-site geometry block are reserved for interactive Incursion objects.
    public const int CargoSource = 9_990;
    public const int CargoDestination = 9_991;
    public const int CargoStack = 9_992;
    public const int OreDeposit = 9_993;
    public const int LogisticsArray = 9_994;

    public static long Object(long site, int slot) =>
        checked(CombatAnomalyRules.FirstObjectId + (site - CombatAnomalyRules.FirstSiteId) * AnomalyEncounterRules.ObjectsPerSite + slot);

    public static long Site(long itemId) =>
        CombatAnomalyRules.FirstSiteId + (itemId - CombatAnomalyRules.FirstObjectId) / AnomalyEncounterRules.ObjectsPerSite;

    public static bool IsCargo(long itemId) => IsInSiteRange(itemId) && Slot(itemId) is CargoSource or CargoDestination;

    public static bool IsOre(long itemId) => IsInSiteRange(itemId) && Slot(itemId) == OreDeposit;

    public static bool IsArray(long itemId) => IsInSiteRange(itemId) && Slot(itemId) == LogisticsArray;

    private static bool IsInSiteRange(long itemId) => itemId is >= CombatAnomalyRules.FirstObjectId and < CombatAnomalyRules.FirstNpcId;

    private static long Slot(long itemId) => (itemId - CombatAnomalyRules.FirstObjectId) % AnomalyEncounterRules.ObjectsPerSite;
}
