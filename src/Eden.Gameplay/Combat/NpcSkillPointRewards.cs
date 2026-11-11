using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Combat;

public static class NpcSkillPointRewards
{
    // Eden progression policy: award 0.25% of the gross ISK bounty as additional SP.
    public const int IskPerSkillPoint = ActivitySkillPointRewards.IskPerSkillPoint;

    public static long FromBounty(long bountyCents, decimal multiplier = 1)
    {
        if (bountyCents < 0)
        {
            throw new InvalidDataException("Invalid NPC bounty.");
        }
        // Whole points only; discard the fractional remainder for each kill.
        return ActivitySkillPointRewards.FromValue(bountyCents / (decimal)MarketDefinitions.CentsPerIsk, 1, multiplier);
    }
}
