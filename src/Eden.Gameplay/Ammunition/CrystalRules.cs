using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Ammunition;

public sealed record CrystalWear(double Damage, bool Destroyed);

public static class CrystalRules
{
    // The catalog expresses volatility as a probability, not a percentage.
    // Wear is independent of the hit roll, including missed paid shots.
    public static CrystalWear Use(
        IReadOnlyDictionary<DogmaAttributeId, double> attributes,
        double damage,
        double roll,
        double volatilityMultiplier = 1
    )
    {
        double hitPoints = attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints);
        double chance = attributes.GetValueOrDefault(DogmaAttributeId.CrystalVolatilityChance) * volatilityMultiplier;
        double increment = attributes.GetValueOrDefault(DogmaAttributeId.CrystalVolatilityDamage);
        double enabled = attributes.GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged);
        if (
            !double.IsFinite(hitPoints)
            || hitPoints <= 0
            || !double.IsFinite(damage)
            || damage < 0
            || damage >= hitPoints
            || !double.IsFinite(chance)
            || chance is < 0 or > 1
            || !double.IsFinite(increment)
            || increment < 0
            || enabled is not (0 or 1)
            || !double.IsFinite(roll)
            || roll is < 0 or >= 1
        )
        {
            throw new InvalidDataException("Invalid frequency crystal condition or volatility.");
        }
        if (enabled == 0 || roll >= chance)
        {
            return new(damage, false);
        }
        // Decimal addition avoids giving nominal 4,000-shot faction crystals
        // an extra shot through repeated binary floating-point rounding.
        double current = (double)((decimal)damage + (decimal)increment);
        return new(Math.Min(hitPoints, current), current >= hitPoints);
    }
}
