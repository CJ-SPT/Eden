using Eden.Gameplay.Dogma;
using Eden.Gameplay.Ships;

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
            (int)DroneSkill.MiningDroneOperation => attribute is DogmaAttributeId.MiningAmount or DogmaAttributeId.MaxVelocity,
            (int)DroneSkill.IceHarvestingDroneOperation => attribute is DogmaAttributeId.Duration or DogmaAttributeId.MaxVelocity,
            (int)DroneSkill.RepairDroneOperation => attribute
                is DogmaAttributeId.ShieldBonus
                    or DogmaAttributeId.ArmorRepairAmount
                    or DogmaAttributeId.HullRepairAmount,
            (int)DroneSkill.SalvageDroneOperation => attribute is DogmaAttributeId.AccessDifficultyBonus or DogmaAttributeId.MaxVelocity,
            (int)DroneSkill.DroneNavigation => attribute == DogmaAttributeId.SpeedFactor,
            (int)HullBonusSkill.PropulsionJamming => attribute
                is DogmaAttributeId.ShieldCapacity
                    or DogmaAttributeId.ArmorHitPoints
                    or DogmaAttributeId.StructureHitPoints,
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
