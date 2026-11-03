using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ships;

// Published build-3662168 type IDs. Admission expands only with verified hull capabilities.
public enum TechOneHullType
{
    Rifter = 587,
    Incursus = 594,
    Punisher = 597,
    Merlin = 603,
    Venture = 32880,
}

public sealed record HullAssemblyProfile(int TypeId, InventoryCategory Category, bool Published, int TechLevel);

public static class TechOneHullRules
{
    public static bool CanAssemble(HullAssemblyProfile hull) =>
        hull.Category == InventoryCategory.Ship && hull.Published && hull.TechLevel == 1 && Enum.IsDefined((TechOneHullType)hull.TypeId);
}
