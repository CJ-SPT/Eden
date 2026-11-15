using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Skills;

public static class ActivitySkillPointRewards
{
    // Default Eden conversion: one SP per 400 ISK of activity reference value.
    public const int IskPerSkillPoint = 400;
    public const decimal MinimumIskPerSkillPoint = 0.01m;
    public const decimal MaximumIskPerSkillPoint = 1_000_000_000m;

    public static void ValidateIskPerSkillPoint(decimal value)
    {
        if (value < MinimumIskPerSkillPoint || value > MaximumIskPerSkillPoint)
        {
            throw new InvalidDataException("Reference ISK per SP must be between 0.01 and 1000000000.");
        }
    }

    public static long FromValue(decimal unitValueIsk, long quantity, decimal multiplier = 1, decimal iskPerSkillPoint = IskPerSkillPoint)
    {
        if (unitValueIsk < 0 || quantity < 0)
        {
            throw new InvalidDataException("Invalid activity reward value.");
        }
        WorldTuningRules.ValidateRate(multiplier, true);
        ValidateIskPerSkillPoint(iskPerSkillPoint);
        // Match bounty credits: whole points only, discarding each collection's remainder.
        return checked((long)decimal.Floor(unitValueIsk * quantity * multiplier / iskPerSkillPoint));
    }
}
