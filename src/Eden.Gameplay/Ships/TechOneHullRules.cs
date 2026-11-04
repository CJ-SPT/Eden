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
    Porpoise = 42244,
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
    Hauler = 28,
    Capsule = 29,
    Titan = 30,
    Shuttle = 31,
    Corvette = 237,
    CombatBattlecruiser = 419,
    Destroyer = 420,
    MiningBarge = 463,
    Dreadnought = 485,
    Freighter = 513,
    Carrier = 547,
    Supercarrier = 659,
    CovertOps = 830,
    Interceptor = 831,
    ForceRecon = 833,
    CapitalIndustrial = 883,
    ElectronicAttack = 893,
    HeavyInterdictionCruiser = 894,
    IndustrialCommand = 941,
    PrototypeExploration = 1022,
    AttackBattlecruiser = 1201,
    ForceAuxiliary = 1538,
    ExpeditionCommand = 4902,
    Yacht = 5087,
    AssaultFrigate = 324,
    HeavyAssaultCruiser = 358,
    DeepSpaceTransport = 380,
    EliteBattleship = 381,
    CommandShip = 540,
    Interdictor = 541,
    Exhumer = 543,
    Logistics = 832,
    StealthBomber = 834,
    BlackOps = 898,
    Marauder = 900,
    JumpFreighter = 902,
    CombatReconShip = 906,
    StrategicCruiser = 963,
    BlockadeRunner = 1202,
    ExpeditionFrigate = 1283,
    TacticalDestroyer = 1305,
    LogisticsFrigate = 1527,
    CommandDestroyer = 1534,
    FlagCruiser = 1972,
    LancerDreadnought = 4594,
    CommandCarrier = 5120,
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
        && (Enum.IsDefined((TechOneHullType)hull.TypeId) || IsSmall(hull.Class) || IsCombat(hull.Class) || IsUtility(hull.Class));

    public static bool IsUtility(HullClass hullClass) =>
        hullClass
            is HullClass.Hauler
                or HullClass.MiningBarge
                or HullClass.Freighter
                or HullClass.IndustrialCommand
                or HullClass.ExpeditionCommand
                or HullClass.Yacht;

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
