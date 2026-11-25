using Eden.Gameplay.Contracts;

namespace Eden.Gameplay.Inventory;

public interface ICargoContainerAssembly
{
    IReadOnlyList<InventoryChange> Assemble(long pilotId, long itemId);
}

public static class CargoContainerAssemblyRules
{
    public static bool IsContainer(InventoryItem item) => IsContainer(item.TypeId, item.GroupId);

    public static bool IsContainer(int typeId, int groupId) =>
        typeId != CourierRules.PackageType && Enum.IsDefined((PersonalAssetContainerGroup)groupId);

    // Build 3662168 inventorycommon.const.packagedVolumeOverridesPerType for cargo containers.
    public static double PackagedVolume(int typeId, double volume) =>
        typeId switch
        {
            3297 or 3467 or 17363 or 33011 => 10, // Small standard, secure, audit log or freight.
            3293 or 3466 or 17364 => 33, // Medium standard, secure or audit log.
            3296 or 3465 or 17365 => 65, // Large standard, secure or audit log.
            11488 => 150, // Huge secure container.
            11489 => 300, // Giant secure container.
            33009 => 50, // Medium freight container.
            33007 => 100, // Large freight container.
            33005 => 500, // Huge freight container.
            24445 => 1_200, // Giant freight container.
            33003 => 2_500, // Enormous freight container.
            17366 => 10_000, // Station container.
            17367 => 50_000, // Station vault.
            17368 => 100_000, // Station warehouse.
            _ => volume,
        };

    public static void CheckCapacity(InventoryItem packaged, double assembledVolume, double used, double capacity) =>
        ItemSpawnRules.CheckCapacity(Math.Max(0, assembledVolume - packaged.Volume), 1, used, capacity);
}
