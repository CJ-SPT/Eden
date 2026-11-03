using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ships;

// Published build-3662168 type IDs. Admission expands only with verified hull capabilities.
public enum TechOneHullType
{
    Rifter = 587,
    Incursus = 594,
    Punisher = 597,
    Merlin = 603,
    Zephyr = 2078,
    Echelon = 3532,
    Venture = 32880,
}

public enum HullClass
{
    Unclassified = 0,
    Frigate = 25,
    Shuttle = 31,
    Corvette = 237,
    Destroyer = 420,
    CovertOps = 830,
    Interceptor = 831,
    ElectronicAttack = 893,
    PrototypeExploration = 1022,
}

public sealed record HullAssemblyProfile(
    int TypeId,
    InventoryCategory Category,
    bool Published,
    int TechLevel,
    HullClass Class = HullClass.Unclassified
);

public static class TechOneHullRules
{
    public static bool CanAssemble(HullAssemblyProfile hull) =>
        hull.Category == InventoryCategory.Ship
        && hull.Published
        && hull.TechLevel == 1
        && (Enum.IsDefined((TechOneHullType)hull.TypeId) || IsSmall(hull.Class));

    public static bool IsSmall(HullClass hullClass) =>
        hullClass
            is HullClass.Frigate
                or HullClass.Shuttle
                or HullClass.Corvette
                or HullClass.Destroyer
                or HullClass.CovertOps
                or HullClass.Interceptor
                or HullClass.ElectronicAttack
                or HullClass.PrototypeExploration;
}
