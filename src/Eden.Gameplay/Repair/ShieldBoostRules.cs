using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Repair;

public static class ShieldBoostRules
{
    private const double Percent = 100;

    public static double CapacitorNeed(double basis, double percentPerLevel, int level) => basis * (1 + percentPerLevel * level / Percent);

    public static double RestoredShield(ShipHealth health, ShieldBooster booster)
    {
        if (
            !double.IsFinite(booster.DurationMilliseconds)
            || booster.DurationMilliseconds <= 0
            || !double.IsFinite(booster.ShieldAmount)
            || booster.ShieldAmount <= 0
        )
        {
            throw new InvalidDataException("Invalid shield booster cycle attributes.");
        }
        return Math.Min(health.Defense.Shield, health.Remaining.Shield + booster.ShieldAmount);
    }
}
