using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public enum CombatWeaponType
{
    GatlingRailgun75mm = 561,
    CivilianGatlingRailgun = 3638,
}

public enum CombatSkill
{
    Gunnery = 3300,
    SmallHybridTurret = 3301,
    MediumHybridTurret = 3304,
    LargeHybridTurret = 3307,
    CapitalHybridTurret = 21666,
    SmallRailgunSpecialization = 11082,
    MediumRailgunSpecialization = 12206,
    LargeRailgunSpecialization = 12207,
    SmallBlasterSpecialization = 12210,
    MediumBlasterSpecialization = 12211,
    LargeBlasterSpecialization = 12212,
    CapitalBlasterSpecialization = 41405,
    CapitalRailgunSpecialization = 41406,
    ControlledBursts = 3316,
    RapidFiring = 3310,
    Sharpshooter = 3311,
    MotionPrediction = 3312,
    SurgicalStrike = 3315,
    TrajectoryAnalysis = 3317,
}

public sealed record WeaponSkillModifier(
    CombatSkill Skill,
    DogmaAttributeId Attribute,
    double PerLevel,
    CombatSkill RequiredSkill = CombatSkill.Gunnery,
    int? GroupId = null
);

public static class CombatDefinitions
{
    public const int EnergyWeaponGroup = 53;
    public const int HybridWeaponGroup = 74;

    public static bool IsSupportedHybrid(int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        group == HybridWeaponGroup
        && AmmunitionDefinitions.ValidSize(attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize))
        && attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) is AmmunitionDefinitions.TechOne or AmmunitionDefinitions.TechTwo
        && !InventoryDefinitions.RequiresSpecificShip(attributes);

    public const double WreckingChance = CombatDamage.WreckingChance;
    public const double WreckingMultiplier = CombatDamage.WreckingMultiplier;
    public const double HitQualityOffset = CombatDamage.HitQualityOffset;
    public const double GrazingDamageLimit = 0.625;
    public const double LightDamageLimit = 0.75;
    public const double StandardDamageLimit = 1;
    public const double WellAimedDamageLimit = 1.25;
    public const double GunneryCycleReduction = 0.02;
    public const double RapidFiringCycleReduction = 0.04;
    public const double SurgicalStrikeDamageBonus = 0.03;
    public const double WeaponSkillBonus = 0.05;
    public const double SpecializationDamageBonus = 0.02;

    // The native pilot brain and authoritative shot calculation share these
    // pinned skill effects, including their required-skill or group targets.
    public static IReadOnlyList<WeaponSkillModifier> SkillModifiers { get; } =
        new WeaponSkillModifier[]
        {
            new(CombatSkill.Gunnery, DogmaAttributeId.WeaponCycleTime, -GunneryCycleReduction),
            new(CombatSkill.RapidFiring, DogmaAttributeId.WeaponCycleTime, -RapidFiringCycleReduction),
            new(CombatSkill.Sharpshooter, DogmaAttributeId.MaxRange, WeaponSkillBonus),
            new(CombatSkill.TrajectoryAnalysis, DogmaAttributeId.AccuracyFalloff, WeaponSkillBonus),
            new(CombatSkill.MotionPrediction, DogmaAttributeId.TrackingSpeed, WeaponSkillBonus),
            new(CombatSkill.SurgicalStrike, DogmaAttributeId.DamageMultiplier, SurgicalStrikeDamageBonus, GroupId: EnergyWeaponGroup),
            new(CombatSkill.SurgicalStrike, DogmaAttributeId.DamageMultiplier, SurgicalStrikeDamageBonus, GroupId: HybridWeaponGroup),
            new(CombatSkill.ControlledBursts, DogmaAttributeId.CapacitorNeed, -WeaponSkillBonus),
            new(CombatSkill.SmallHybridTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.SmallHybridTurret),
            new(CombatSkill.MediumHybridTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.MediumHybridTurret),
            new(CombatSkill.LargeHybridTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.LargeHybridTurret),
            new(CombatSkill.CapitalHybridTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.CapitalHybridTurret),
            new(
                CombatSkill.SmallRailgunSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.SmallRailgunSpecialization
            ),
            new(
                CombatSkill.MediumRailgunSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.MediumRailgunSpecialization
            ),
            new(
                CombatSkill.LargeRailgunSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.LargeRailgunSpecialization
            ),
            new(
                CombatSkill.SmallBlasterSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.SmallBlasterSpecialization
            ),
            new(
                CombatSkill.MediumBlasterSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.MediumBlasterSpecialization
            ),
            new(
                CombatSkill.LargeBlasterSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.LargeBlasterSpecialization
            ),
            new(
                CombatSkill.CapitalBlasterSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.CapitalBlasterSpecialization
            ),
            new(
                CombatSkill.CapitalRailgunSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.CapitalRailgunSpecialization
            ),
        };
    public const int ConcordCorporation = 1000125;
    public const long FirstBountyJournalId = 6_000_000_000_000;
}
