namespace Eden.Gameplay.Inventory;

public sealed record RepackageItem(long ItemId, long LocationId);

public sealed record RepackagingState(
    InventoryItem Item,
    bool Supported,
    bool ActiveShip,
    bool HasContents,
    bool InstalledBlueprint,
    double ArmorDamage,
    double StructureDamage
);

public interface IPilotRepackaging
{
    IReadOnlyList<InventoryChange> Repackage(long pilotId, int stationId, IReadOnlyList<RepackageItem> items);
}

public static class RepackagingRules
{
    public static void Check(RepackagingState state)
    {
        if (
            !state.Supported
            || state.Item.CategoryId is not ((int)InventoryCategory.Ship or (int)InventoryCategory.Module)
            || state.ActiveShip
            || state.HasContents
            || state.InstalledBlueprint
            || state.Item.Online
            || state.ArmorDamage > 0
            || state.StructureDamage > 0
            || state.Item.CrystalDamage > 0
        )
        {
            throw new InvalidDataException("Repackage an inactive, empty, unfitted and undamaged supported ship or module in Item Hangar.");
        }
    }
}
