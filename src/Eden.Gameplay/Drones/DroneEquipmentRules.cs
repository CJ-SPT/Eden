using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Drones;

public static class DroneEquipmentRules
{
    // Build 3662168 effects 6555-6559 also select the Fighters skill.
    // Parsing those declared modifiers does not provide a fighter consumer.
    public const int FighterOperationSkill = 23069;

    public static bool SupportsOwnerModifier(int skill, DogmaAttributeId attribute) =>
        skill switch
        {
            (int)DroneSkill.Drones => attribute
                is DogmaAttributeId.MaxVelocity
                    or DogmaAttributeId.DamageMultiplier
                    or DogmaAttributeId.MaxRange
                    or DogmaAttributeId.AccuracyFalloff
                    or DogmaAttributeId.TrackingSpeed,
            FighterOperationSkill => attribute
                is DogmaAttributeId.MaxVelocity
                    or DogmaAttributeId.ShieldCapacity
                    or DogmaAttributeId.FighterMissilesDamageMultiplier
                    or DogmaAttributeId.FighterMissilesRange
                    or DogmaAttributeId.FighterMissilesExplosionRadius
                    or DogmaAttributeId.FighterMissilesExplosionVelocity
                    or DogmaAttributeId.FighterTurretOptimal
                    or DogmaAttributeId.FighterTurretFalloff
                    or DogmaAttributeId.FighterTurretDamageMultiplier
                    or DogmaAttributeId.FighterTurretTracking
                    or DogmaAttributeId.FighterAttackMissileDamageMultiplier
                    or DogmaAttributeId.FighterAttackMissileOptimal
                    or DogmaAttributeId.FighterAttackMissileFalloff
                    or DogmaAttributeId.FighterAttackMissileExplosionRadius
                    or DogmaAttributeId.FighterAttackMissileExplosionVelocity,
            _ => false,
        };
}
