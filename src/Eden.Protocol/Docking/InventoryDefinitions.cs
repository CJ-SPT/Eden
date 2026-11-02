namespace Eden.Protocol.Docking;

// Build 3662168: inventorycommon.const and dogma data in the pinned catalog.
public enum InventoryFlag
{
    AutoFit = 0,
    Hangar = 4,
    Cargo = 5,
    HighSlot0 = 27,
    HighSlot7 = 34,
}

public enum DogmaAttributeId
{
    IsOnline = 2,
    Damage = 3,
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
    CapacitorRechargeTime = 55,
    MaxTargetRange = 76,
    TurretSlotsLeft = 102,
    RequiredSkill1 = 182,
    RequiredSkill2 = 183,
    RequiredSkill3 = 184,
    MaxLockedTargets = 192,
    ShieldCapacity = 263,
    ShieldCharge = 264,
    ArmorHitPoints = 265,
    SkillTimeConstant = 275,
    RequiredSkill1Level = 277,
    RequiredSkill2Level = 278,
    RequiredSkill3Level = 279,
    ShieldRechargeTime = 479,
    CapacitorCapacity = 482,
    SignatureRadius = 552,
    ScanResolution = 564,
    RequiredSkill4 = 1285,
    RequiredSkill4Level = 1286,
    RequiredSkill5Level = 1287,
    RequiredSkill6Level = 1288,
    RequiredSkill5 = 1289,
    RequiredSkill6 = 1290,
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
    CpuManagement = 3426,
}

public enum DogmaEffect
{
    Online = 16,
}

public enum StationService
{
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
