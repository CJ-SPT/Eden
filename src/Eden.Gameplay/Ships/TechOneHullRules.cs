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
    Nestor = 33472,
    Cenotaph = 85086,
    Tholos = 85087,
}

public enum HullClass
{
    Unclassified = 0,
    Frigate = 25,
    Cruiser = 26,
    Battleship = 27,
    Shuttle = 31,
    Corvette = 237,
    CombatBattlecruiser = 419,
    Destroyer = 420,
    CovertOps = 830,
    Interceptor = 831,
    ForceRecon = 833,
    ElectronicAttack = 893,
    HeavyInterdictionCruiser = 894,
    PrototypeExploration = 1022,
    AttackBattlecruiser = 1201,
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
        && (Enum.IsDefined((TechOneHullType)hull.TypeId) || IsSmall(hull.Class) || IsCombat(hull.Class));

    public static bool IsCombat(HullClass hullClass) =>
        hullClass
            is HullClass.Cruiser
                or HullClass.Battleship
                or HullClass.CombatBattlecruiser
                or HullClass.AttackBattlecruiser
                or HullClass.ForceRecon
                or HullClass.HeavyInterdictionCruiser;

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
