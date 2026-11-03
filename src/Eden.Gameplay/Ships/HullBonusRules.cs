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
    CombatDrones,
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
}

// Catalog selectors are IDs, independent of a pilot's trained level.
public enum HullBonusSkill
{
    SmallHybridTurret = 3301,
    SmallProjectileTurret = 3302,
    SmallEnergyTurret = 3303,
    MissileLauncherOperation = 3319,
    Mining = 3386,
    RepairSystems = 3393,
    Science = 3402,
    Astrometrics = 3412,
    ShieldOperation = 3416,
    EnergyPulseWeapons = 3421,
    ShieldEmissionSystems = 3422,
    WeaponDisruption = 3434,
    PropulsionJamming = 3435,
    Drones = 3436,
    SalvageDroneOperation = 3440,
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
    LightDroneOperation = 24241,
    InfomorphPsychology = 24242,
    CapitalRemoteArmorRepairSystems = 24568,
    GasCloudHarvesting = 25544,
    Salvaging = 25863,
    SmallPrecursorWeapon = 47870,
    SmallVortonProjector = 55034,
}

public enum HullBonusEquipmentGroup
{
    RemoteShieldBooster = 41,
    WarpScrambler = 52,
    ProjectileWeapon = 55,
    StasisWeb = 65,
    EnergyNosferatu = 68,
    EnergyNeutralizer = 71,
    Ecm = 201,
    SensorDampener = 208,
    RemoteArmorRepairer = 325,
    ArmorPlate = 329,
    ScanProbeLauncher = 481,
    DataMiner = 538,
    AncillaryRemoteShieldBooster = 1697,
    AncillaryRemoteArmorRepairer = 1698,
    InterdictionNullifier = 4117,
}

public sealed record HullBonusTarget(ModifierDomain Domain, ModifierSelector Selector, int? Selection, DogmaAttributeId Attribute);

public sealed record HullEffectCapability(int EffectId, HullCapability Capability, bool Operational);

public static class HullBonusRules
{
    // These role fittings have no modifierInfo entries in the pinned hull effects.
    public static IReadOnlyList<HullCapability> DeferredRoles(int typeId) =>
        (TechOneHullType)typeId switch
        {
            TechOneHullType.Zephyr => [HullCapability.Probes],
            TechOneHullType.Echelon => [HullCapability.Hacking],
            _ => [],
        };

    // Skill/group IDs and selectors are from the pinned build's hull effects.
    public static HullCapability Capability(HullBonusTarget target) =>
        target.Selector switch
        {
            ModifierSelector.Item => target.Domain switch
            {
                ModifierDomain.Ship => HullCapability.HullAttributes,
                ModifierDomain.Character
                    when target.Attribute is DogmaAttributeId.MiningDroneAmountPercent or DogmaAttributeId.DroneControlDistance =>
                    HullCapability.CombatDrones,
                _ => HullCapability.Unclassified,
            },
            ModifierSelector.OwnerRequiredSkill => target.Selection switch
            {
                int skill when MissileDefinitions.SupportsOwnerSkill(skill) => HullCapability.Missiles,
                (int)HullBonusSkill.Science or (int)HullBonusSkill.Astrometrics => HullCapability.Probes,
                (int)HullBonusSkill.PropulsionJamming => HullCapability.Interdiction,
                (int)HullBonusSkill.Drones or (int)HullBonusSkill.SalvageDroneOperation or (int)HullBonusSkill.LightDroneOperation =>
                    HullCapability.CombatDrones,
                _ => HullCapability.Unclassified,
            },
            ModifierSelector.Group => (HullBonusEquipmentGroup?)target.Selection switch
            {
                HullBonusEquipmentGroup.ProjectileWeapon => HullCapability.Turrets,
                HullBonusEquipmentGroup.WarpScrambler or HullBonusEquipmentGroup.StasisWeb => HullCapability.Tackle,
                HullBonusEquipmentGroup.ArmorPlate => HullCapability.ArmorPlates,
                HullBonusEquipmentGroup.RemoteShieldBooster
                or HullBonusEquipmentGroup.RemoteArmorRepairer
                or HullBonusEquipmentGroup.AncillaryRemoteShieldBooster
                or HullBonusEquipmentGroup.AncillaryRemoteArmorRepairer => HullCapability.RemoteRepair,
                HullBonusEquipmentGroup.EnergyNosferatu or HullBonusEquipmentGroup.EnergyNeutralizer => HullCapability.EnergyWarfare,
                HullBonusEquipmentGroup.Ecm or HullBonusEquipmentGroup.SensorDampener => HullCapability.ElectronicWarfare,
                HullBonusEquipmentGroup.ScanProbeLauncher => HullCapability.Probes,
                HullBonusEquipmentGroup.DataMiner => HullCapability.Hacking,
                HullBonusEquipmentGroup.InterdictionNullifier => HullCapability.Interdiction,
                _ => HullCapability.Unclassified,
            },
            ModifierSelector.RequiredSkill => (HullBonusSkill?)target.Selection switch
            {
                HullBonusSkill.SmallHybridTurret or HullBonusSkill.SmallProjectileTurret or HullBonusSkill.SmallEnergyTurret =>
                    HullCapability.Turrets,
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
                HullBonusSkill.WeaponDisruption or HullBonusSkill.TargetPainting => HullCapability.ElectronicWarfare,
                HullBonusSkill.Cloaking => HullCapability.Cloaking,
                HullBonusSkill.Archaeology or HullBonusSkill.Hacking => HullCapability.Hacking,
                HullBonusSkill.IceHarvesting => HullCapability.IceMining,
                HullBonusSkill.CynosuralFieldTheory => HullCapability.CynosuralFields,
                HullBonusSkill.InfomorphPsychology => HullCapability.Entosis,
                HullBonusSkill.GasCloudHarvesting => HullCapability.GasMining,
                HullBonusSkill.Salvaging => HullCapability.Salvaging,
                HullBonusSkill.SmallPrecursorWeapon => HullCapability.Disintegrators,
                HullBonusSkill.SmallVortonProjector => HullCapability.VortonProjectors,
                _ => HullCapability.Unclassified,
            },
            _ => HullCapability.Unclassified,
        };

    public static bool Operational(HullCapability capability) =>
        capability
            is HullCapability.HullAttributes
                or HullCapability.Turrets
                or HullCapability.Missiles
                or HullCapability.OreMining
                or HullCapability.LocalArmorRepair
                or HullCapability.LocalShieldBoost
                or HullCapability.Propulsion
                or HullCapability.Tackle
                or HullCapability.ArmorPlates;
}
