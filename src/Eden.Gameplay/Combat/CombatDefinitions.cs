using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

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
        && attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize) == Eden.Gameplay.Ammunition.AmmunitionDefinitions.SmallChargeSize
        && attributes.GetValueOrDefault(DogmaAttributeId.TechLevel) == Eden.Gameplay.Ammunition.AmmunitionDefinitions.TechOne;

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
        };
    public const int ConcordCorporation = 1000125;
    public const long FirstBountyJournalId = 6_000_000_000_000;
}
