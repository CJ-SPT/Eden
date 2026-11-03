using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Combat;

public sealed record MissileWarhead(DamageVector Damage, double ExplosionRadius, double ExplosionVelocity, double ReductionExponent);

public static class MissileRules
{
    public static MissileWarhead Warhead(IReadOnlyDictionary<DogmaAttributeId, double> attributes, double damageMultiplier = 1)
    {
        MissileWarhead result = new(
            new(
                attributes.GetValueOrDefault(DogmaAttributeId.EmDamage),
                attributes.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
                attributes.GetValueOrDefault(DogmaAttributeId.KineticDamage),
                attributes.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
            ),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosionRadius),
            attributes.GetValueOrDefault(DogmaAttributeId.ExplosionVelocity),
            attributes.GetValueOrDefault(DogmaAttributeId.MissileReductionExponent, 1)
        );
        if (
            !double.IsFinite(damageMultiplier)
            || damageMultiplier <= 0
            || !result.Damage.IsValid
            || result.Damage.Total <= 0
            || !double.IsFinite(result.ExplosionRadius)
            || result.ExplosionRadius <= 0
            || !double.IsFinite(result.ExplosionVelocity)
            || result.ExplosionVelocity <= 0
            || !double.IsFinite(result.ReductionExponent)
            || result.ReductionExponent <= 0
        )
        {
            throw new InvalidDataException("Invalid missile warhead attributes.");
        }
        var damage = result.Damage.Scale(damageMultiplier);
        if (!damage.IsValid)
        {
            throw new InvalidDataException("Missile damage exceeded the finite range.");
        }
        return result with { Damage = damage };
    }

    public static double Application(MissileWarhead missile, double signatureRadius, double speed)
    {
        if (
            !double.IsFinite(signatureRadius)
            || signatureRadius <= 0
            || !double.IsFinite(speed)
            || speed < 0
            || !double.IsFinite(missile.ExplosionRadius)
            || missile.ExplosionRadius <= 0
            || !double.IsFinite(missile.ExplosionVelocity)
            || missile.ExplosionVelocity <= 0
            || !double.IsFinite(missile.ReductionExponent)
            || missile.ReductionExponent <= 0
        )
        {
            throw new InvalidDataException("Invalid missile target signature or speed.");
        }
        double size = signatureRadius / missile.ExplosionRadius;
        // Build 3662168 stores the already transformed exponent in 1353;
        // applying the historical log(drf)/log(5.5) conversion again is wrong.
        double velocity = speed > 0 ? Math.Pow(size * missile.ExplosionVelocity / speed, missile.ReductionExponent) : 1;
        return Math.Clamp(Math.Min(size, velocity), 0, 1);
    }
}
