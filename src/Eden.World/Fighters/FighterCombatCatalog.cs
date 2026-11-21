using Eden.Data.Catalog;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;

namespace Eden.World.Fighters;

internal static class FighterCombatCatalog
{
    // The standard attack squadron uses the attack missile, in slot 0 on build 3662168.
    private const int SpaceSuperiorityRole = 1;
    private const int LightAttackRole = 2;

    // Pinned default for attribute 2189. Fighter hulls explicitly override it.
    internal const double DefaultAntiFighterResistance = 0.05;
    private const int HeavyAttackRole = 4;

    internal static DroneProfile Profile(
        CatalogItemDefinition type,
        ClientTypes? source,
        IReadOnlyDictionary<DogmaAttributeId, double> values
    )
    {
        double Value(DogmaAttributeId id) => values.GetValueOrDefault(id);
        if (FighterDeckRules.Class(values) == FighterClass.Support)
        {
            return FighterSupportCatalog.Profile(type, source, values);
        }
        var (fighterClass, role) = (FighterDeckRules.Class(values), Value(DogmaAttributeId.FighterSquadronRole));
        bool superiority = fighterClass == FighterClass.Light && role == SpaceSuperiorityRole;
        if (
            source?.Published != 1
            || (
                !superiority && (fighterClass, role) is not ((FighterClass.Light, LightAttackRole) or (FighterClass.Heavy, HeavyAttackRole))
            )
            || type.Mass is not > 0
            || type.Radius is not > 0
            || source.Volume is not > 0
        )
        {
            throw new InvalidDataException("Only supported published fighter squadrons can launch.");
        }
        var damage = new DamageVector(
            Attribute(DogmaAttributeId.FighterAttackEmDamage, DogmaAttributeId.FighterMissilesEmDamage),
            Attribute(DogmaAttributeId.FighterAttackExplosiveDamage, DogmaAttributeId.FighterMissilesExplosiveDamage),
            Attribute(DogmaAttributeId.FighterAttackKineticDamage, DogmaAttributeId.FighterMissilesKineticDamage),
            Attribute(DogmaAttributeId.FighterAttackThermalDamage, DogmaAttributeId.FighterMissilesThermalDamage)
        ).Scale(Attribute(DogmaAttributeId.FighterAttackMissileDamageMultiplier, DogmaAttributeId.FighterMissilesDamageMultiplier));
        MissileWarhead warhead = new(
            damage,
            Attribute(DogmaAttributeId.FighterAttackMissileExplosionRadius, DogmaAttributeId.FighterMissilesExplosionRadius),
            Attribute(DogmaAttributeId.FighterAttackMissileExplosionVelocity, DogmaAttributeId.FighterMissilesExplosionVelocity),
            Math.Log(Attribute(DogmaAttributeId.FighterAttackMissileReductionFactor, DogmaAttributeId.FighterMissilesReductionFactor))
                / Math.Log(
                    Attribute(
                        DogmaAttributeId.FighterAttackMissileReductionSensitivity,
                        DogmaAttributeId.FighterMissilesReductionSensitivity
                    )
                )
        );
        double speed = Value(DogmaAttributeId.MaxVelocity);
        double cycle = Attribute(DogmaAttributeId.FighterAttackMissileDuration, DogmaAttributeId.FighterMissilesDuration);
        double range = Attribute(DogmaAttributeId.FighterAttackMissileOptimal, DogmaAttributeId.FighterMissilesRange);
        double falloff = superiority ? 0 : Value(DogmaAttributeId.FighterAttackMissileFalloff);
        if (
            !double.IsFinite(damage.Total)
            || damage.Total <= 0
            || !double.IsFinite(falloff)
            || falloff < 0
            || !superiority && falloff == 0
            || new[] { speed, cycle, range, warhead.ExplosionRadius, warhead.ExplosionVelocity, warhead.ReductionExponent }.Any(n =>
                !double.IsFinite(n) || n <= 0
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
            FighterGroup: type.GroupId,
            SpaceSuperiority: superiority
        );

        double Attribute(DogmaAttributeId attack, DogmaAttributeId antiFighter) => Value(superiority ? antiFighter : attack);
    }
}
