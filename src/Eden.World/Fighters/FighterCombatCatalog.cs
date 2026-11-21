using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;

namespace Eden.World.Fighters;

internal static class FighterCombatCatalog
{
    // The standard attack squadron uses the attack missile, in slot 0 on build 3662168.
    private const int LightAttackRole = 2;
    private const int HeavyAttackRole = 4;

    internal static DroneProfile Profile(
        CatalogItemDefinition type,
        ClientTypes? source,
        IReadOnlyDictionary<DogmaAttributeId, double> values
    )
    {
        double Value(DogmaAttributeId id) => values.GetValueOrDefault(id);
        if (
            source?.Published != 1
            || (FighterDeckRules.Class(values), Value(DogmaAttributeId.FighterSquadronRole))
                is not ((FighterClass.Light, LightAttackRole) or (FighterClass.Heavy, HeavyAttackRole))
            || type.Mass is not > 0
            || type.Radius is not > 0
            || source.Volume is not > 0
        )
        {
            throw new InvalidDataException("Only published light and heavy attack fighter squadrons can launch.");
        }
        var damage = new DamageVector(
            Value(DogmaAttributeId.FighterAttackEmDamage),
            Value(DogmaAttributeId.FighterAttackExplosiveDamage),
            Value(DogmaAttributeId.FighterAttackKineticDamage),
            Value(DogmaAttributeId.FighterAttackThermalDamage)
        ).Scale(Value(DogmaAttributeId.FighterAttackMissileDamageMultiplier));
        MissileWarhead warhead = new(
            damage,
            Value(DogmaAttributeId.FighterAttackMissileExplosionRadius),
            Value(DogmaAttributeId.FighterAttackMissileExplosionVelocity),
            Math.Log(Value(DogmaAttributeId.FighterAttackMissileReductionFactor))
                / Math.Log(Value(DogmaAttributeId.FighterAttackMissileReductionSensitivity))
        );
        double speed = Value(DogmaAttributeId.MaxVelocity);
        double cycle = Value(DogmaAttributeId.FighterAttackMissileDuration);
        double range = Value(DogmaAttributeId.FighterAttackMissileOptimal);
        double falloff = Value(DogmaAttributeId.FighterAttackMissileFalloff);
        if (
            !double.IsFinite(damage.Total)
            || damage.Total <= 0
            || new[] { speed, cycle, range, falloff, warhead.ExplosionRadius, warhead.ExplosionVelocity, warhead.ReductionExponent }.Any(
                n => !double.IsFinite(n) || n <= 0
            )
        )
        {
            throw new InvalidDataException("Invalid attack fighter catalog data.");
        }
        return new(
            type.Id,
            source.Volume.Value,
            0,
            type.Mass.Value,
            (float)type.Radius.Value,
            (float)Value(DogmaAttributeId.Agility),
            (float)speed,
            (float)speed,
            Value(DogmaAttributeId.FighterSquadronOrbitRange),
            range,
            falloff,
            0,
            0,
            cycle,
            damage,
            ShipHealth.DefenseFrom(values),
            FighterAttack: warhead,
            FighterGroup: type.GroupId
        );
    }
}
