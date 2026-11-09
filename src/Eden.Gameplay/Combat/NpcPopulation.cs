using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public enum NpcRole
{
    Pirate,
    Navy,
    Customs,
    Convoy,
    MiningMiner,
    MiningHauler,
    MiningDefender,
    MiningLogistics,
    MissionStructure,
}

public enum NpcAnchorKind
{
    Belt,
    Gate,
    Station,
    Anomaly,
    Mission,
}

public static class NpcPopulationRules
{
    // Some rogue drones have a complete direct weapon and no gfxTurretID.
    // The optional turret model must not decide whether they can deal damage.
    public static bool HasDirectWeapon(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        attributes.GetValueOrDefault(DogmaAttributeId.WeaponCycleTime) > 0
        && attributes.GetValueOrDefault(DogmaAttributeId.DamageMultiplier) > 0
        && (
            attributes.GetValueOrDefault(DogmaAttributeId.EmDamage)
            + attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage)
            + attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage)
            + attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        ) > 0;

    public static bool DetectsPilots(NpcRole role) => role == NpcRole.Pirate;

    public static bool RewardsPirateKill(NpcRole role) => role == NpcRole.Pirate;

    public static bool IsMining(NpcRole role) =>
        role is NpcRole.MiningMiner or NpcRole.MiningHauler or NpcRole.MiningDefender or NpcRole.MiningLogistics;

    public static bool IsCivilian(NpcRole role) => role is NpcRole.Convoy or NpcRole.MiningMiner or NpcRole.MiningHauler;

    public static bool AwardsBounty(NpcRole role) => role is NpcRole.Pirate or NpcRole.MiningMiner or NpcRole.MiningHauler;

    public static double DetectionRange(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0,
        double? acquisitionRange = null
    ) =>
        Math.Min(
            NpcEncounterDefinitions.VisibilityRange,
            acquisitionRange
                ?? Math.Max(
                    NpcEncounterDefinitions.DetectionRange,
                    Math.Max(attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange), missileRange)
                )
        );

    public static double Leash(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double missileRange = 0,
        double? acquisitionRange = null
    ) => Math.Max(NpcEncounterDefinitions.ChaseLeash, DetectionRange(attributes, missileRange, acquisitionRange) * 2);
}
