using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Structures;

public sealed record StructurePointDefense(double Range, double Duration, double Capacitor, int Rounds, DamageVector Damage)
{
    public static StructurePointDefense Define(
        IReadOnlyDictionary<DogmaAttributeId, double> module,
        IReadOnlyDictionary<DogmaAttributeId, double> charge
    )
    {
        double range = module.GetValueOrDefault(DogmaAttributeId.EmpFieldRange);
        double duration = module.GetValueOrDefault(DogmaAttributeId.Duration);
        double capacitor = module.GetValueOrDefault(DogmaAttributeId.CapacitorNeed);
        double rounds = module.GetValueOrDefault(DogmaAttributeId.ChargeRate);
        double multiplier = module.GetValueOrDefault(DogmaAttributeId.DamageMultiplier);
        // Build 3662168 stores one pulse's damage on the charge. ChargeRate determines only magazine consumption.
        var damage = new DamageVector(
            charge.GetValueOrDefault(DogmaAttributeId.EmDamage),
            charge.GetValueOrDefault(DogmaAttributeId.ExplosiveDamage),
            charge.GetValueOrDefault(DogmaAttributeId.KineticDamage),
            charge.GetValueOrDefault(DogmaAttributeId.ThermalDamage)
        ).Scale(multiplier);
        if (
            !double.IsFinite(range)
            || range <= 0
            || !double.IsFinite(duration)
            || duration <= 0
            || !double.IsFinite(capacitor)
            || capacitor < 0
            || rounds is < 1 or > int.MaxValue
            || rounds != Math.Truncate(rounds)
            || !damage.IsValid
            || damage.Total <= 0
        )
        {
            throw new InvalidDataException("The point defense battery has an invalid firing profile.");
        }
        return new(range, duration, capacitor, (int)rounds, damage);
    }
}
