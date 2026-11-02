namespace Eden.Protocol.Docking;

public enum InventoryCategory
{
    Station = 3,
    Ship = 6,
    Module = 7,
    Asteroid = 25,
}

// Build 3662168: inventorycommon.const and dogma data in the pinned catalog.
public enum InventoryFlag
{
    AutoFit = 0,
    Hangar = 4,
    Cargo = 5,
    HighSlot0 = 27,
    HighSlot1 = 28,
    HighSlot7 = 34,
    GeneralMiningHold = 134,
}

public enum InventoryLocation
{
    Graveyard = 8,
}

public enum DogmaAttributeId
{
    IsOnline = 2,
    Damage = 3,
    CapacitorNeed = 6,
    StructureHitPoints = 9,
    PowerOutput = 11,
    HighSlots = 14,
    PowerLoad = 15,
    CapacitorCharge = 18,
    Power = 30,
    MaxVelocity = 37,
    CargoCapacity = 38,
    CpuOutput = 48,
    CpuLoad = 49,
    Cpu = 50,
    WeaponCycleTime = 51,
    MaxRange = 54,
    CapacitorRechargeTime = 55,
    DamageMultiplier = 64,
    Agility = 70,
    Duration = 73,
    MaxTargetRange = 76,
    MiningAmount = 77,
    TurretSlotsLeft = 102,
    StructureKineticResonance = 109,
    StructureThermalResonance = 110,
    StructureExplosiveResonance = 111,
    StructureEmResonance = 113,
    EmDamage = 114,
    ExplosiveDamage = 116,
    KineticDamage = 117,
    ThermalDamage = 118,
    BuiltInCharge = 127,
    AccuracyFalloff = 158,
    TrackingSpeed = 160,
    RequiredSkill1 = 182,
    RequiredSkill2 = 183,
    RequiredSkill3 = 184,
    MaxLockedTargets = 192,
    MiningRoleYieldMultiplier = 207,
    EntityTurretType = 245,
    EntityAttackRange = 247,
    ShieldCapacity = 263,
    ShieldCharge = 264,
    ArmorHitPoints = 265,
    ArmorDamage = 266,
    ArmorEmResonance = 267,
    ArmorExplosiveResonance = 268,
    ArmorKineticResonance = 269,
    ArmorThermalResonance = 270,
    ShieldEmResonance = 271,
    ShieldExplosiveResonance = 272,
    ShieldKineticResonance = 273,
    ShieldThermalResonance = 274,
    SkillTimeConstant = 275,
    RefiningYieldMutator = 379,
    RequiredSkill1Level = 277,
    RequiredSkill2Level = 278,
    RequiredSkill3Level = 279,
    ShieldRechargeTime = 479,
    EntityAttackDelayMinimum = 475,
    EntityKillBounty = 481,
    CapacitorCapacity = 482,
    SignatureRadius = 552,
    ScanResolution = 564,
    WeaponSignatureResolution = 620,
    ReprocessingSkillType = 790,
    RequiredSkill4 = 1285,
    RequiredSkill4Level = 1286,
    RequiredSkill5Level = 1287,
    RequiredSkill6Level = 1288,
    RequiredSkill5 = 1289,
    RequiredSkill6 = 1290,
    GeneralMiningHoldCapacity = 1556,
    RookieArmorResistanceBonus = 1825,
    RookieShieldResistanceBonus = 1829,
    MiningFrigateYieldBonus = 1842,
    MiningCriticalChance = 5967,
    MiningCriticalBonusYield = 5969,
}

public enum InventoryColumn
{
    Location = 3,
    Flag = 4,
    Quantity = 5,
    StackSize = 9,
    Singleton = 10,
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
    CivilianWeapon = 10,
    Online = 16,
    MiningLaser = 67,
}

public enum StationService
{
    Reprocessing = 5,
    Market = 7,
    Repair = 13,
    Fitting = 17,
}

public static class InventoryDefinitions
{
    public const int MiningLaserGroup = 54;
    public const int CivilianMinerType = 3651;
    public const double OutputBonusPerSkillLevel = 0.05;
    public const double ResourceTolerance = 1e-9;
    public const int AssembledQuantity = -1;
    public const int InfiniteEffectDuration = -1;
    public const long FirstOwnedItemId = 2000000000000;
    public static bool IsHighSlot(int flag) => flag is >= (int)InventoryFlag.HighSlot0 and <= (int)InventoryFlag.HighSlot7;
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
