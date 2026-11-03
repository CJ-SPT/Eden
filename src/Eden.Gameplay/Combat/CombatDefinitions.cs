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
    SmallEnergyTurret = 3303,
    MediumEnergyTurret = 3306,
    LargeEnergyTurret = 3309,
    CapitalEnergyTurret = 20327,
    SmallBeamLaserSpecialization = 11083,
    MediumBeamLaserSpecialization = 12204,
    LargeBeamLaserSpecialization = 12205,
    CapitalBeamLaserSpecialization = 41408,
    SmallPulseLaserSpecialization = 12213,
    MediumPulseLaserSpecialization = 12214,
    LargePulseLaserSpecialization = 12215,
    CapitalPulseLaserSpecialization = 41407,
    SmallHybridTurret = 3301,
    SmallProjectileTurret = 3302,
    MediumProjectileTurret = 3305,
    LargeProjectileTurret = 3308,
    CapitalProjectileTurret = 21667,
    SmallAutocannonSpecialization = 11084,
    MediumAutocannonSpecialization = 12208,
    LargeAutocannonSpecialization = 12209,
    SmallArtillerySpecialization = 12201,
    MediumArtillerySpecialization = 12202,
    LargeArtillerySpecialization = 12203,
    CapitalAutocannonSpecialization = 41403,
    CapitalArtillerySpecialization = 41404,
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
    public const int ProjectileWeaponGroup = 55;

    public static bool IsSupportedAmmoTurret(int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        group is EnergyWeaponGroup or HybridWeaponGroup or ProjectileWeaponGroup
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
            new(CombatSkill.SmallEnergyTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.SmallEnergyTurret),
            new(CombatSkill.MediumEnergyTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.MediumEnergyTurret),
            new(CombatSkill.LargeEnergyTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.LargeEnergyTurret),
            new(CombatSkill.CapitalEnergyTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.CapitalEnergyTurret),
            new(
                CombatSkill.SmallBeamLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.SmallBeamLaserSpecialization
            ),
            new(
                CombatSkill.MediumBeamLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.MediumBeamLaserSpecialization
            ),
            new(
                CombatSkill.LargeBeamLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.LargeBeamLaserSpecialization
            ),
            new(
                CombatSkill.CapitalBeamLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.CapitalBeamLaserSpecialization
            ),
            new(
                CombatSkill.SmallPulseLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.SmallPulseLaserSpecialization
            ),
            new(
                CombatSkill.MediumPulseLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.MediumPulseLaserSpecialization
            ),
            new(
                CombatSkill.LargePulseLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.LargePulseLaserSpecialization
            ),
            new(
                CombatSkill.CapitalPulseLaserSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.CapitalPulseLaserSpecialization
            ),
            new(CombatSkill.Gunnery, DogmaAttributeId.WeaponCycleTime, -GunneryCycleReduction),
            new(CombatSkill.RapidFiring, DogmaAttributeId.WeaponCycleTime, -RapidFiringCycleReduction),
            new(CombatSkill.Sharpshooter, DogmaAttributeId.MaxRange, WeaponSkillBonus),
            new(CombatSkill.TrajectoryAnalysis, DogmaAttributeId.AccuracyFalloff, WeaponSkillBonus),
            new(CombatSkill.MotionPrediction, DogmaAttributeId.TrackingSpeed, WeaponSkillBonus),
            new(CombatSkill.SurgicalStrike, DogmaAttributeId.DamageMultiplier, SurgicalStrikeDamageBonus, GroupId: EnergyWeaponGroup),
            new(CombatSkill.SurgicalStrike, DogmaAttributeId.DamageMultiplier, SurgicalStrikeDamageBonus, GroupId: HybridWeaponGroup),
            new(CombatSkill.SurgicalStrike, DogmaAttributeId.DamageMultiplier, SurgicalStrikeDamageBonus, GroupId: ProjectileWeaponGroup),
            new(CombatSkill.SmallProjectileTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.SmallProjectileTurret),
            new(
                CombatSkill.MediumProjectileTurret,
                DogmaAttributeId.DamageMultiplier,
                WeaponSkillBonus,
                CombatSkill.MediumProjectileTurret
            ),
            new(CombatSkill.LargeProjectileTurret, DogmaAttributeId.DamageMultiplier, WeaponSkillBonus, CombatSkill.LargeProjectileTurret),
            new(
                CombatSkill.CapitalProjectileTurret,
                DogmaAttributeId.DamageMultiplier,
                WeaponSkillBonus,
                CombatSkill.CapitalProjectileTurret
            ),
            new(
                CombatSkill.SmallAutocannonSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.SmallAutocannonSpecialization
            ),
            new(
                CombatSkill.MediumAutocannonSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.MediumAutocannonSpecialization
            ),
            new(
                CombatSkill.LargeAutocannonSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.LargeAutocannonSpecialization
            ),
            new(
                CombatSkill.SmallArtillerySpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.SmallArtillerySpecialization
            ),
            new(
                CombatSkill.MediumArtillerySpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.MediumArtillerySpecialization
            ),
            new(
                CombatSkill.LargeArtillerySpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.LargeArtillerySpecialization
            ),
            new(
                CombatSkill.CapitalAutocannonSpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.CapitalAutocannonSpecialization
            ),
            new(
                CombatSkill.CapitalArtillerySpecialization,
                DogmaAttributeId.DamageMultiplier,
                SpecializationDamageBonus,
                CombatSkill.CapitalArtillerySpecialization
            ),
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
