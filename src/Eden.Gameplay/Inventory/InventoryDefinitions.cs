using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

public enum InventoryCategory
{
    Celestial = 2,
    Station = 3,
    Material = 4,
    Accessories = 5,
    Ship = 6,
    Module = 7,
    Charge = 8,
    Blueprint = 9,
    Entity = 11,
    Skill = 16,
    Commodity = 17,
    Drone = 18,
    Implant = 20,
    Deployable = 22,
    Starbase = 23,
    Reaction = 24,
    Asteroid = 25,
    Apparel = 30,
    Subsystem = 32,
    Relic = 34,
    Decryptor = 35,
    InfrastructureUpgrade = 39,
    SovereigntyStructure = 40,
    PlanetaryIndustry = 41,
    PlanetaryResource = 42,
    PlanetaryCommodity = 43,
    Orbitals = 46,
    SpecialEditionAssets = 63,
    Structure = 65,
    StructureModule = 66,
    Fighter = 87,
    ShipSkin = 91,
    ExpertSystem = 2100,
    Personalization = 2118,
    ColonyResource = 2143,
}

// Build 3662168: inventorycommon.const and dogma data in the pinned catalog.
public enum InventoryFlag
{
    AutoFit = 0,
    Hangar = 4,
    Cargo = 5,
    LowSlot0 = 11,
    LowSlot7 = 18,
    MediumSlot0 = 19,
    MediumSlot7 = 26,
    HighSlot0 = 27,
    HighSlot1 = 28,
    HighSlot7 = 34,
    Locked = 63,
    Unlocked = 64,
    DroneBay = 87,
    Implant = 89,
    ShipMaintenanceBay = 90,
    RigSlot0 = 92,
    RigSlot1 = 93,
    RigSlot2 = 94,
    SubsystemSlot0 = 125,
    SubsystemSlot3 = 128,
    FuelBay = 133,
    GeneralMiningHold = 134,
    GasHold = 135,
    MineralHold = 136,
    AmmoHold = 143,
    CommandCenterHold = 148,
    PlanetaryCommoditiesHold = 149,
    QuafeBay = 154,
    FleetHangar = 155,
    HiddenModifiers = 156,
    FighterBay = 158,
    ServiceSlot0 = 164,
    ServiceSlot7 = 171,
    CorpseBay = 174,
    FrigateEscapeBay = 179,
    IceHold = 181,
    MobileDepotHold = 183,
    InfrastructureHold = 185,
    ExpeditionHold = 188,
}

public enum InventoryLocation
{
    Graveyard = 8,
}

public enum FittingSkill
{
    PowerGridManagement = 3413,
    CapacitorSystemsOperation = 3417,
    CapacitorManagement = 3418,
    CpuManagement = 3426,
}

public enum DogmaEffect
{
    Hacking = 1738,
    ShipScan = 46,
    CargoScan = 47,
    PassiveTargeting = 54,
    AutomaticTargeting = 55,
    TractorBeam = 2255,
    Salvaging = 2757,
    BehaviorArmorRepair = 6741,
    BehaviorShieldRepair = 6742,
    BehaviorWeb = 6743,
    BehaviorDisrupt = 6744,
    BehaviorScramble = 6745,
    BehaviorGuidanceDisrupt = 6746,
    BehaviorTrackingDisrupt = 6747,
    BehaviorPaint = 6754,
    BehaviorDamp = 6755,
    BehaviorEcm = 6757,
    PlayerEcm = 6470,
    SensorDampener = 6422,
    TrackingDisruptor = 6424,
    GuidanceDisruptor = 6423,
    TargetPainter = 6425,
    NpcEcm = 6695,

    // Build-3662168 CONCORD Captain dogma effects.
    ConcordDroneBandwidth = 3661,
    ConcordTargetJam = 3710,
    ConcordWarpScramble = 3713,
    ConcordWeb = 3714,
    NpcSensorDampening = 1878,
    NpcTrackingDisruption = 6846,
    NpcTargetPainting = 1879,
    NpcEnergyNeutralization = 6691,
    NpcWebifier = 575,
    NpcWarpDisruption = 563,
    RemoteCapacitorTransfer = 6184,
    RemoteSensorBooster = 6427,
    RemoteTrackingComputer = 6428,
    RemoteHullRepair = 6185,
    RemoteShieldBoost = 6186,
    RemoteArmorRepair = 6188,
    AncillaryRemoteArmorRepair = 6651,
    AncillaryRemoteShieldBoost = 6652,
    MutadaptiveRemoteArmorRepair = 7166,
    AssaultDamageControl = 7012,
    EnergyNeutralizer = 6187,
    EnergyNosferatu = 6197,
    ShieldBoosting = 4,
    AncillaryShieldBoosting = 4936,
    AncillaryArmorRepair = 5275,
    MissileLaunching = 9,
    LaserFired = 10,
    UseMissiles = 101,
    AutoTargetingMissile = 104,
    TrackingComputer = 4559,
    SensorBooster = 2670,
    MissileGuidanceComputer = 6135,
    OmnidirectionalTrackingLink = 6557,
    StasisWebifier = 6426,
    WarpDisruptor = 39,
    WarpScrambler = 5934,
    WarpCoreStabilizer = 8121,
    SignatureSuppressionOnline = 8108,
    SignatureSuppression = 8109,
    LowPower = 11,
    HighPower = 12,
    MediumPower = 13,
    Online = 16,
    RigSlot = 2663,
    StructureRepair = 26,
    ArmorRepair = 27,
    CapacitorBoosting = 48,
    CapacitorCapacityMultiply = 58,
    ActiveShieldResistance = 5230,
    ActiveArmorResistance = 5231,
    LauncherFitted = 40,
    TurretFitted = 42,
    Microwarpdrive = 6730,
    ProjectileFired = 34,
    MiningLaser = 67,
    MiningClouds = 2726,
    MiningCrystalInformation = 1212,
    Afterburner = 6731,
}

public enum StationService
{
    Reprocessing = 5,
    Market = 7,
    Repair = 13,
    Manufacturing = 14,
    Laboratory = 15,
    Fitting = 17,
    Insurance = 21,
    JumpCloneFacility = 24,
    LoyaltyStore = 25,
}

public static class InventoryDefinitions
{
    public static IReadOnlyList<DogmaAttributeId> ShipRestrictions { get; } =
    [
        DogmaAttributeId.CanFitShipGroup1,
        DogmaAttributeId.CanFitShipGroup2,
        DogmaAttributeId.CanFitShipGroup3,
        DogmaAttributeId.CanFitShipGroup4,
        DogmaAttributeId.CanFitShipGroup5,
        DogmaAttributeId.CanFitShipGroup6,
        DogmaAttributeId.CanFitShipGroup7,
        DogmaAttributeId.CanFitShipGroup8,
        DogmaAttributeId.CanFitShipGroup9,
        DogmaAttributeId.CanFitShipGroup10,
        DogmaAttributeId.CanFitShipGroup11,
        DogmaAttributeId.CanFitShipGroup12,
        DogmaAttributeId.CanFitShipGroup13,
        DogmaAttributeId.CanFitShipGroup14,
        DogmaAttributeId.CanFitShipGroup15,
        DogmaAttributeId.CanFitShipGroup16,
        DogmaAttributeId.CanFitShipGroup17,
        DogmaAttributeId.CanFitShipGroup18,
        DogmaAttributeId.CanFitShipGroup19,
        DogmaAttributeId.CanFitShipGroup20,
        DogmaAttributeId.CanFitShipType1,
        DogmaAttributeId.CanFitShipType2,
        DogmaAttributeId.CanFitShipType3,
        DogmaAttributeId.CanFitShipType4,
        DogmaAttributeId.CanFitShipType5,
        DogmaAttributeId.CanFitShipType6,
        DogmaAttributeId.CanFitShipType7,
        DogmaAttributeId.CanFitShipType8,
        DogmaAttributeId.CanFitShipType9,
        DogmaAttributeId.CanFitShipType10,
        DogmaAttributeId.CanFitShipType11,
        DogmaAttributeId.CanFitShipType12,
    ];

    public static bool RequiresSpecificShip(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        ShipRestrictions.Any(attribute => attributes.GetValueOrDefault(attribute) > 0);

    public static bool CanFitShip(IReadOnlyDictionary<DogmaAttributeId, double> attributes, int typeId, int groupId) =>
        ShipFittingRestrictions.Read(attributes).Allows(typeId, groupId);

    public const int MiningLaserGroup = 54;
    public const int StripMinerGroup = 464;
    public const int ModulatedMinerGroup = 483;
    public const int GasScoopGroup = 737;
    public const int GasHarvesterGroup = 4138;

    public static bool IsOreMiningGroup(int groupId) => groupId is MiningLaserGroup or StripMinerGroup or ModulatedMinerGroup;

    public static bool IsMiningGroup(int groupId) => IsOreMiningGroup(groupId) || groupId is GasScoopGroup or GasHarvesterGroup;

    public const int CivilianMinerType = 3651;
    public const double OutputBonusPerSkillLevel = 0.05;
    public const double ResourceTolerance = 1e-9;
    public const long FirstOwnedItemId = 2000000000000;
    public const int SlotsPerRack = 8; // Pinned inventory flags reserve eight slots per rack.

    public static bool IsLowSlot(int flag) => flag is >= (int)InventoryFlag.LowSlot0 and <= (int)InventoryFlag.LowSlot7;

    public static bool IsHighSlot(int flag) => flag is >= (int)InventoryFlag.HighSlot0 and <= (int)InventoryFlag.HighSlot7;

    public static bool IsMediumSlot(int flag) => flag is >= (int)InventoryFlag.MediumSlot0 and <= (int)InventoryFlag.MediumSlot7;

    public static bool IsRigSlot(int flag) => flag is >= (int)InventoryFlag.RigSlot0 and <= (int)InventoryFlag.RigSlot2;

    public static bool IsFittedSlot(int flag) => IsLowSlot(flag) || IsHighSlot(flag) || IsMediumSlot(flag) || IsRigSlot(flag);

    public static readonly (DogmaAttributeId Skill, DogmaAttributeId Level)[] RequiredSkills =
    [
        (DogmaAttributeId.RequiredSkill1, DogmaAttributeId.RequiredSkill1Level),
        (DogmaAttributeId.RequiredSkill2, DogmaAttributeId.RequiredSkill2Level),
        (DogmaAttributeId.RequiredSkill3, DogmaAttributeId.RequiredSkill3Level),
        (DogmaAttributeId.RequiredSkill4, DogmaAttributeId.RequiredSkill4Level),
        (DogmaAttributeId.RequiredSkill5, DogmaAttributeId.RequiredSkill5Level),
        (DogmaAttributeId.RequiredSkill6, DogmaAttributeId.RequiredSkill6Level),
    ];
}
