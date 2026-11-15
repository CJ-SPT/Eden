using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Combat;

public static class NpcSkillPointRewards
{
    // Eden progression policy: use the scaled gross ISK bounty before tax or fleet sharing.
    public const int IskPerSkillPoint = ActivitySkillPointRewards.IskPerSkillPoint;
    public const decimal DefaultPercent = 0.25m;
    public const decimal MaximumPercent = 100;
    private const decimal PercentDivisor = 100;

    public static void ValidatePercent(decimal value)
    {
        if (value < 0 || value > MaximumPercent)
        {
            throw new InvalidDataException("NPC kill base SP rate must be between 0 and 100 percent.");
        }
    }

    public static long FromBounty(long bountyCents, decimal multiplier = 1, decimal basePercent = DefaultPercent)
    {
        if (bountyCents < 0)
        {
            throw new InvalidDataException("Invalid NPC bounty.");
        }
        ValidatePercent(basePercent);
        // Keep the percentage exact until the shared final whole-point rounding.
        decimal percentageValue = bountyCents / (decimal)MarketDefinitions.CentsPerIsk * basePercent;
        return ActivitySkillPointRewards.FromValue(percentageValue, 1, multiplier, PercentDivisor);
    }
}
