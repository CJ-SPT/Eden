using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Combat;

public sealed record DisintegratorSpool(double PerCycle, double Maximum)
{
    public double Multiplier(int paidCycles) => 1 + Math.Min(Maximum, PerCycle * Math.Max(0, paidCycles));
}

public sealed record VortonProfile(double ArcRange, int Targets, MissileWarhead Warhead, int Size);

public static class AdvancedWeaponRules
{
    public static IReadOnlyList<int> Skills { get; } =
    [
        (int)Ships.HullBonusSkill.SmallPrecursorWeapon,
        (int)Ships.HullBonusSkill.MediumPrecursorWeapon,
        (int)Ships.HullBonusSkill.LargePrecursorWeapon,
        47873,
        47874,
        47875,
        52998,
        92541,
        (int)Ships.HullBonusSkill.LargeVortonProjector,
        54827,
        54828,
        54829,
        54840,
        54841,
        55033,
        (int)Ships.HullBonusSkill.SmallVortonProjector,
        (int)Ships.HullBonusSkill.MediumVortonProjector,
        55511,
    ];

    public static DogmaEffect Effect(int group, bool ammoFed) =>
        group switch
        {
            CombatDefinitions.DisintegratorGroup => DogmaEffect.Disintegrator,
            CombatDefinitions.VortonGroup => DogmaEffect.Vorton,
            CombatDefinitions.EnergyWeaponGroup => DogmaEffect.LaserFired,
            _ => ammoFed ? DogmaEffect.ProjectileFired : DogmaEffect.LaserFired,
        };

    public static WeaponActivation Activation(DogmaEffect effect) =>
        effect switch
        {
            DogmaEffect.Disintegrator => WeaponActivation.Disintegrator,
            DogmaEffect.Vorton => WeaponActivation.Vorton,
            DogmaEffect.ProjectileFired => WeaponActivation.AmmoFed,
            _ => WeaponActivation.Laser,
        };

    public static DisintegratorSpool Spool(IReadOnlyDictionary<DogmaAttributeId, double> attributes)
    {
        double step = attributes.GetValueOrDefault(DogmaAttributeId.DisintegratorSpoolPerCycle);
        double maximum = attributes.GetValueOrDefault(DogmaAttributeId.DisintegratorSpoolMaximum);
        if (!double.IsFinite(step) || step <= 0 || !double.IsFinite(maximum) || maximum < step)
        {
            throw new InvalidDataException("Invalid entropic disintegrator spool.");
        }
        return new(step, maximum);
    }

    public static VortonProfile Vorton(IReadOnlyDictionary<DogmaAttributeId, double> attributes, DamageVector damage)
    {
        double range = attributes.GetValueOrDefault(DogmaAttributeId.VortonArcRange);
        double count = attributes.GetValueOrDefault(DogmaAttributeId.VortonArcTargets);
        double radius = attributes.GetValueOrDefault(DogmaAttributeId.ExplosionRadius);
        double velocity = attributes.GetValueOrDefault(DogmaAttributeId.ExplosionVelocity);
        double exponent = attributes.GetValueOrDefault(DogmaAttributeId.MissileReductionExponent);
        double size = attributes.GetValueOrDefault(DogmaAttributeId.ChargeSize);
        if (
            !double.IsFinite(range)
            || range <= 0
            || count is < 1 or > 10
            || count != Math.Truncate(count)
            || !double.IsFinite(radius)
            || radius <= 0
            || !double.IsFinite(velocity)
            || velocity <= 0
            || !double.IsFinite(exponent)
            || exponent <= 0
            || size is not (1 or 2 or 3)
        )
        {
            throw new InvalidDataException("Invalid Vorton application attributes.");
        }
        return new(range, (int)count, new(damage, radius, velocity, exponent), (int)size);
    }
}
