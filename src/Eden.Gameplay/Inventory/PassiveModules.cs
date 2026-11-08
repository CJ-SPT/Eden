using Eden.Gameplay.Dogma;
using Eden.Gameplay.Repair;

namespace Eden.Gameplay.Inventory;

public enum ModifierDomain
{
    Item,
    Ship,
    Character,
}

public enum ModifierSelector
{
    Item,
    Group,
    RequiredSkill,
    OwnerRequiredSkill,
    Location,
}

public enum PassiveEffectPhase
{
    Fitted = 0,
    Active = 1,
    Targeted = 2,
    Online = 4,
}

public sealed record PassiveModifier(
    ModifierDomain Domain,
    ModifierSelector Selector,
    int? SelectionId,
    DogmaAttributeId Attribute,
    DogmaAttributeId SourceAttribute,
    DogmaOperation Operation,
    PassiveEffectPhase Phase
);

public sealed record FittingSkillBonus(int SkillId, PassiveModifier Modifier, double Value);

// Build 3662168 inventory groups and skill types.
public enum PassiveModuleGroup
{
    ShieldExtender = 38,
    ShieldRecharger = 39,
    CapacitorRecharger = 43,
    ShieldPowerRelay = 57,
    Gyrostabilizer = 59,
    DamageControl = 60,
    CapacitorBattery = 61,
    ReinforcedBulkhead = 78,
    ArmorCoating = 98,
    HeatSink = 205,
    SignalAmplifier = 210,
    SignalDistortionAmplifier = 514,
    TrackingEnhancer = 211,
    CpuEnhancer = 285,
    ShieldResistanceAmplifier = 295,
    MagneticFieldStabilizer = 302,
    EnergizedArmorMembrane = 326,
    ArmorPlate = 329,
    ShieldBoostAmplifier = 338,
    AuxiliaryPowerCore = 339,
    BallisticControl = 367,
    MiningUpgrade = 546,
    DroneNavigationComputer = 644,
    DroneDamageAmplifier = 645,
    DroneLinkAugmentor = 647,
    OmnidirectionalTrackingEnhancer = 1292,
    InertialStabilizer = 762,
    NanofiberInternalStructure = 763,
    OverdriveInjector = 764,
    ExpandedCargohold = 765,
    PowerDiagnosticSystem = 766,
    CapacitorPowerRelay = 767,
    CapacitorFluxCoil = 768,
    ReactorControlUnit = 769,
    ShieldFluxCoil = 770,
    MissileGuidanceEnhancer = 1395,
    HyperspatialAccelerator = 1289,
}

public enum PassiveModuleSkill
{
    RepairSystems = 3393,
    CapitalRepairSystems = 21803,
    ShieldUpgrades = 3425,
    EnergyGridUpgrades = 3424,
    ElectronicsUpgrades = 3432,
    ArmorLayering = 33078,
    MiningUpgrades = 22578,
    EmArmorCompensation = 22806,
    ExplosiveArmorCompensation = 22807,
    KineticArmorCompensation = 22808,
    ThermalArmorCompensation = 22809,
    EmShieldCompensation = 12365,
    KineticShieldCompensation = 12366,
    ExplosiveShieldCompensation = 12367,
    ThermalShieldCompensation = 11566,
    WeaponUpgrades = 3318,
    AdvancedWeaponUpgrades = 11207,
    PropulsionJamming = 3435,
    SignatureMasking = 4411,
}

public static class PassiveModules
{
    // A family alone never grants support: every catalog effect must also be handled.
    public static IReadOnlySet<int> Groups { get; } = Enum.GetValues<PassiveModuleGroup>().Select(g => (int)g).ToHashSet();

    // Existing weapon/navigation/hull skill rules retain their current single owner.
    public static IReadOnlyList<int> Skills { get; } =
        Enum.GetValues<PassiveModuleSkill>()
            .Select(skill => (int)skill)
            .Concat(Enum.GetValues<ShieldBoostSkill>().Select(skill => (int)skill))
            .ToArray();
}
