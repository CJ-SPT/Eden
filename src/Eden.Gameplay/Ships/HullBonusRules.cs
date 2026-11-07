using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Ships;

public enum HullCapability
{
    Unclassified,
    HullAttributes,
    Turrets,
    Missiles,
    OreMining,
    LocalArmorRepair,
    LocalShieldBoost,
    Propulsion,
    Tackle,
    ArmorPlates,
    ShieldExtenders,
    ReinforcedBulkheads,
    CombatDrones,
    MiningDrones,
    UtilityDrones,
    Probes,
    Salvaging,
    ElectronicWarfare,
    RemoteRepair,
    EnergyWarfare,
    Cloaking,
    CynosuralFields,
    IceMining,
    GasMining,
    Hacking,
    Entosis,
    Smartbombs,
    Disintegrators,
    VortonProjectors,
    Interdiction,
    CommandBursts,
    ShipMaintenance,
    BreacherPods,
    MicroJumpDrives,
    TractorBeams,
    IndustrialCores,
    CapitalFlexShieldHardening,
    FleetAccess,
    FleetRefitting,
    FrigateEscapeRecovery,
    MiningCrystals,
    DeepCoreMining,
}

// Catalog selectors are IDs, independent of a pilot's trained level.
public enum HullBonusSkill
{
    SmallHybridTurret = 3301,
    SmallProjectileTurret = 3302,
    SmallEnergyTurret = 3303,
    MediumHybridTurret = 3304,
    MediumProjectileTurret = 3305,
    MediumEnergyTurret = 3306,
    LargeHybridTurret = 3307,
    LargeProjectileTurret = 3308,
    LargeEnergyTurret = 3309,
    MissileLauncherOperation = 3319,
    Leadership = 3348,
    ShieldCommand = 3350,
    MiningForeman = 22536,
    InformationCommand = 20494,
    ExpeditionCommand = 89611,
    IndustrialReconfiguration = 58956,
    IceHarvestingDroneOperation = 43702,
    Mining = 3386,
    RepairSystems = 3393,
    Science = 3402,
    Astrometrics = 3412,
    ShieldOperation = 3416,
    EnergyPulseWeapons = 3421,
    ShieldEmissionSystems = 3422,
    CapacitorEmissionSystems = 3423,
    ElectronicWarfare = 3427,
    WeaponDisruption = 3434,
    PropulsionJamming = 3435,
    Drones = 3436,
    MiningDroneOperation = 3438,
    SalvageDroneOperation = 3440,
    HeavyDroneOperation = 3441,
    Navigation = 3449,
    Afterburner = 3450,
    HighSpeedManeuvering = 3454,
    Cloaking = 11579,
    Archaeology = 13278,
    RemoteArmorRepairSystems = 16069,
    IceHarvesting = 16281,
    TargetPainting = 19921,
    CynosuralFieldTheory = 21603,
    Hacking = 21718,
    SentryDroneInterfacing = 23594,
    LightDroneOperation = 24241,
    InfomorphPsychology = 24242,
    CapitalRemoteArmorRepairSystems = 24568,
    GasCloudHarvesting = 25544,
    Salvaging = 25863,
    MediumDroneOperation = 33699,
    SmallPrecursorWeapon = 47870,
    MediumPrecursorWeapon = 47871,
    LargePrecursorWeapon = 47872,
    LargeVortonProjector = 54826,
    SmallVortonProjector = 55034,
    MediumVortonProjector = 55035,
}

public enum HullBonusEquipmentGroup
{
    ShieldExtender = 38,
    RemoteShieldBooster = 41,
    WarpScrambler = 52,
    ProjectileWeapon = 55,
    StasisWeb = 65,
    RemoteCapacitorTransmitter = 67,
    EnergyNosferatu = 68,
    EnergyNeutralizer = 71,
    ReinforcedBulkhead = 78,
    BurstJammer = 80,
    Ecm = 201,
    SensorDampener = 208,
    RemoteArmorRepairer = 325,
    ArmorPlate = 329,
    TargetPainter = 379,
    ScanProbeLauncher = 481,
    CruiseMissileLauncher = 506,
    TorpedoLauncher = 508,
    HeavyMissileLauncher = 510,
    RapidLightMissileLauncher = 511,
    DataMiner = 538,
    HeavyAssaultMissileLauncher = 771,
    WarpDisruptionFieldGenerator = 899,
    RapidHeavyMissileLauncher = 1245,
    TractorBeam = 650,
    CloakingDevice = 330,
    CapitalFlexShieldHardener = 1700,
    AncillaryRemoteShieldBooster = 1697,
    AncillaryRemoteArmorRepairer = 1698,
    MutadaptiveRemoteArmorRepairer = 2018,
    InterdictionNullifier = 4117,
}

public sealed record HullBonusTarget(ModifierDomain Domain, ModifierSelector Selector, int? Selection, DogmaAttributeId Attribute);

public sealed record HullEffectCapability(int EffectId, HullCapability Capability, bool Operational);

public static class HullBonusRules
{
    // These role fittings have no modifierInfo entries in the pinned hull effects.
    public static IReadOnlyList<HullCapability> DeferredRoles(int typeId, HullClass hullClass = HullClass.Unclassified)
    {
        IReadOnlyList<HullCapability> roles = (TechOneHullType)typeId switch
        {
            TechOneHullType.Zephyr => [HullCapability.Probes],
            TechOneHullType.Echelon => [HullCapability.Hacking],
            TechOneHullType.Cenotaph or TechOneHullType.Tholos => [HullCapability.BreacherPods],
            _ => [],
        };
        if (hullClass == HullClass.MiningBarge)
        {
            roles = [.. roles, HullCapability.MiningCrystals, HullCapability.DeepCoreMining];
        }
        // Build 3662168 modules 4383/33915 admit these combat groups. Their
        // fitting restrictions do not appear in the hull's modifierInfo list.
        return
            hullClass
                is HullClass.Battleship
                    or HullClass.CombatBattlecruiser
                    or HullClass.AttackBattlecruiser
                    or HullClass.ExpeditionCommand
            ? [.. roles, HullCapability.MicroJumpDrives]
            : roles;
    }

    // Skill/group IDs and selectors are from the pinned build's hull effects.
    public static HullCapability Capability(HullBonusTarget target) =>
        target.Selector switch
        {
            ModifierSelector.Item => target.Domain switch
            {
                ModifierDomain.Ship => HullCapability.HullAttributes,
                ModifierDomain.Character
                    when target.Attribute is DogmaAttributeId.DroneControlDistance or DogmaAttributeId.MaxActiveDrones =>
                    HullCapability.CombatDrones,
                ModifierDomain.Character when target.Attribute == DogmaAttributeId.MiningDroneAmountPercent => HullCapability.MiningDrones,
                _ => HullCapability.Unclassified,
            },
            ModifierSelector.OwnerRequiredSkill => target.Selection switch
            {
                int skill when MissileDefinitions.SupportsOwnerSkill(skill) => HullCapability.Missiles,
                (int)HullBonusSkill.Science or (int)HullBonusSkill.Astrometrics => HullCapability.Probes,
                (int)HullBonusSkill.PropulsionJamming
                    when target.Attribute
                        is DogmaAttributeId.ShieldCapacity
                            or DogmaAttributeId.ArmorHitPoints
                            or DogmaAttributeId.StructureHitPoints => HullCapability.UtilityDrones,
                (int)HullBonusSkill.PropulsionJamming => HullCapability.Interdiction,
                (int)HullBonusSkill.MiningDroneOperation or (int)HullBonusSkill.IceHarvestingDroneOperation => HullCapability.MiningDrones,
                (int)HullBonusSkill.SalvageDroneOperation => HullCapability.UtilityDrones,
                (int)HullBonusSkill.Drones
                or (int)HullBonusSkill.LightDroneOperation
                or (int)HullBonusSkill.MediumDroneOperation
                or (int)HullBonusSkill.HeavyDroneOperation
                or (int)HullBonusSkill.SentryDroneInterfacing => DroneCapability(target.Attribute),
                _ => HullCapability.Unclassified,
            },
            ModifierSelector.Group => (HullBonusEquipmentGroup?)target.Selection switch
            {
                HullBonusEquipmentGroup.TractorBeam => HullCapability.TractorBeams,
                HullBonusEquipmentGroup.CloakingDevice => HullCapability.Cloaking,
                HullBonusEquipmentGroup.CapitalFlexShieldHardener => HullCapability.CapitalFlexShieldHardening,
                HullBonusEquipmentGroup.ProjectileWeapon => HullCapability.Turrets,
                HullBonusEquipmentGroup.WarpScrambler or HullBonusEquipmentGroup.StasisWeb => HullCapability.Tackle,
                HullBonusEquipmentGroup.ArmorPlate => HullCapability.ArmorPlates,
                HullBonusEquipmentGroup.ShieldExtender => HullCapability.ShieldExtenders,
                HullBonusEquipmentGroup.ReinforcedBulkhead => HullCapability.ReinforcedBulkheads,
                HullBonusEquipmentGroup.CruiseMissileLauncher
                or HullBonusEquipmentGroup.TorpedoLauncher
                or HullBonusEquipmentGroup.HeavyMissileLauncher
                or HullBonusEquipmentGroup.RapidLightMissileLauncher
                or HullBonusEquipmentGroup.HeavyAssaultMissileLauncher
                or HullBonusEquipmentGroup.RapidHeavyMissileLauncher => HullCapability.Missiles,
                HullBonusEquipmentGroup.RemoteShieldBooster
                or HullBonusEquipmentGroup.RemoteArmorRepairer
                or HullBonusEquipmentGroup.AncillaryRemoteShieldBooster
                or HullBonusEquipmentGroup.AncillaryRemoteArmorRepairer
                or HullBonusEquipmentGroup.MutadaptiveRemoteArmorRepairer => HullCapability.RemoteRepair,
                HullBonusEquipmentGroup.EnergyNosferatu
                or HullBonusEquipmentGroup.EnergyNeutralizer
                or HullBonusEquipmentGroup.RemoteCapacitorTransmitter => HullCapability.EnergyWarfare,
                HullBonusEquipmentGroup.Ecm
                or HullBonusEquipmentGroup.SensorDampener
                or HullBonusEquipmentGroup.TargetPainter
                or HullBonusEquipmentGroup.BurstJammer => HullCapability.ElectronicWarfare,
                HullBonusEquipmentGroup.ScanProbeLauncher => HullCapability.Probes,
                HullBonusEquipmentGroup.DataMiner => HullCapability.Hacking,
                HullBonusEquipmentGroup.InterdictionNullifier or HullBonusEquipmentGroup.WarpDisruptionFieldGenerator =>
                    HullCapability.Interdiction,
                _ => HullCapability.Unclassified,
            },
            ModifierSelector.RequiredSkill => (HullBonusSkill?)target.Selection switch
            {
                HullBonusSkill.SmallHybridTurret
                or HullBonusSkill.SmallProjectileTurret
                or HullBonusSkill.SmallEnergyTurret
                or HullBonusSkill.MediumHybridTurret
                or HullBonusSkill.MediumProjectileTurret
                or HullBonusSkill.MediumEnergyTurret
                or HullBonusSkill.LargeHybridTurret
                or HullBonusSkill.LargeProjectileTurret
                or HullBonusSkill.LargeEnergyTurret => HullCapability.Turrets,
                HullBonusSkill.MissileLauncherOperation => HullCapability.Missiles,
                HullBonusSkill.Mining => HullCapability.OreMining,
                HullBonusSkill.RepairSystems => HullCapability.LocalArmorRepair,
                HullBonusSkill.ShieldOperation => HullCapability.LocalShieldBoost,
                HullBonusSkill.PropulsionJamming or HullBonusSkill.Navigation => HullCapability.Tackle,
                HullBonusSkill.Afterburner or HullBonusSkill.HighSpeedManeuvering => HullCapability.Propulsion,
                HullBonusSkill.Astrometrics => HullCapability.Probes,
                HullBonusSkill.EnergyPulseWeapons => HullCapability.Smartbombs,
                HullBonusSkill.ShieldEmissionSystems
                or HullBonusSkill.RemoteArmorRepairSystems
                or HullBonusSkill.CapitalRemoteArmorRepairSystems => HullCapability.RemoteRepair,
                HullBonusSkill.CapacitorEmissionSystems => HullCapability.EnergyWarfare,
                HullBonusSkill.WeaponDisruption or HullBonusSkill.TargetPainting or HullBonusSkill.ElectronicWarfare =>
                    HullCapability.ElectronicWarfare,
                HullBonusSkill.Leadership
                or HullBonusSkill.ShieldCommand
                or HullBonusSkill.MiningForeman
                or HullBonusSkill.InformationCommand
                or HullBonusSkill.ExpeditionCommand => HullCapability.CommandBursts,
                HullBonusSkill.IndustrialReconfiguration => HullCapability.IndustrialCores,
                HullBonusSkill.Cloaking => HullCapability.Cloaking,
                HullBonusSkill.Archaeology or HullBonusSkill.Hacking => HullCapability.Hacking,
                HullBonusSkill.IceHarvesting => HullCapability.IceMining,
                HullBonusSkill.CynosuralFieldTheory => HullCapability.CynosuralFields,
                HullBonusSkill.InfomorphPsychology => HullCapability.Entosis,
                HullBonusSkill.GasCloudHarvesting => HullCapability.GasMining,
                HullBonusSkill.Salvaging => HullCapability.Salvaging,
                HullBonusSkill.SmallPrecursorWeapon or HullBonusSkill.MediumPrecursorWeapon or HullBonusSkill.LargePrecursorWeapon =>
                    HullCapability.Disintegrators,
                HullBonusSkill.SmallVortonProjector or HullBonusSkill.MediumVortonProjector or HullBonusSkill.LargeVortonProjector =>
                    HullCapability.VortonProjectors,
                _ => HullCapability.Unclassified,
            },
            _ => HullCapability.Unclassified,
        };

    private static HullCapability DroneCapability(DogmaAttributeId attribute) =>
        attribute switch
        {
            DogmaAttributeId.ShieldCapacity
            or DogmaAttributeId.ArmorHitPoints
            or DogmaAttributeId.StructureHitPoints
            or DogmaAttributeId.DamageMultiplier
            or DogmaAttributeId.EmDamage
            or DogmaAttributeId.ExplosiveDamage
            or DogmaAttributeId.KineticDamage
            or DogmaAttributeId.ThermalDamage
            or DogmaAttributeId.TrackingSpeed
            or DogmaAttributeId.MaxRange
            or DogmaAttributeId.AccuracyFalloff
            or DogmaAttributeId.MaxVelocity => HullCapability.CombatDrones,
            DogmaAttributeId.ShieldBonus
            or DogmaAttributeId.ArmorRepairAmount
            or DogmaAttributeId.HullRepairAmount
            or DogmaAttributeId.SpeedFactor
            or DogmaAttributeId.EcmJamDuration => HullCapability.UtilityDrones,
            _ => HullCapability.Unclassified,
        };

    public static bool Operational(HullCapability capability, int? equipmentGroup = null) =>
        (
            capability == HullCapability.RemoteRepair
            && (
                equipmentGroup
                is null
                    or (int)HullBonusEquipmentGroup.RemoteShieldBooster
                    or (int)HullBonusEquipmentGroup.RemoteArmorRepairer
            )
        )
        || equipmentGroup != (int)HullBonusEquipmentGroup.BurstJammer
            && capability
                is HullCapability.HullAttributes
                    or HullCapability.Turrets
                    or HullCapability.Missiles
                    or HullCapability.OreMining
                    or HullCapability.LocalArmorRepair
                    or HullCapability.LocalShieldBoost
                    or HullCapability.Propulsion
                    or HullCapability.Tackle
                    or HullCapability.ElectronicWarfare
                    or HullCapability.EnergyWarfare
                    or HullCapability.ArmorPlates
                    or HullCapability.ShieldExtenders
                    or HullCapability.ReinforcedBulkheads
                    or HullCapability.CombatDrones;
}
