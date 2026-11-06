using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public enum NpcRole
{
    Pirate,
    Navy,
    Customs,
    Convoy,
}

public enum NpcAnchorKind
{
    Belt,
    Gate,
    Station,
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

    public static double DetectionRange(IReadOnlyDictionary<DogmaAttributeId, double> attributes, double missileRange = 0) =>
        Math.Min(
            NpcEncounterDefinitions.VisibilityRange,
            Math.Max(
                NpcEncounterDefinitions.DetectionRange,
                Math.Max(attributes.GetValueOrDefault(DogmaAttributeId.EntityAttackRange), missileRange)
            )
        );

    public static double Leash(IReadOnlyDictionary<DogmaAttributeId, double> attributes, double missileRange = 0) =>
        Math.Max(NpcEncounterDefinitions.ChaseLeash, DetectionRange(attributes, missileRange) * 2);
}
