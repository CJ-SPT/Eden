using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Skills;

public static class ActivitySkillPointRewards
{
    // Eden progression policy: 0.25% of the activity's gross reference ISK value.
    public const int IskPerSkillPoint = 400;

    public static long FromValue(decimal unitValueIsk, long quantity, decimal multiplier = 1)
    {
        if (unitValueIsk < 0 || quantity < 0)
        {
            throw new InvalidDataException("Invalid activity reward value.");
        }
        WorldTuningRules.ValidateRate(multiplier, true);
        // Match bounty credits: whole points only, discarding each collection's remainder.
        return checked((long)decimal.Floor(unitValueIsk * quantity * multiplier / IskPerSkillPoint));
    }
}
