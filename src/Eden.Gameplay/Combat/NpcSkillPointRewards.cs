using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Combat;

public static class NpcSkillPointRewards
{
    // Eden progression policy: award 0.25% of the gross ISK bounty as additional SP.
    public const int IskPerSkillPoint = 400;

    public static long FromBounty(long bountyCents, decimal multiplier = 1)
    {
        if (bountyCents < 0)
        {
            throw new InvalidDataException("Invalid NPC bounty.");
        }
        // Whole points only; discard the fractional remainder for each kill.
        WorldTuningRules.ValidateRate(multiplier, true);
        return checked((long)decimal.Floor(bountyCents * multiplier / (IskPerSkillPoint * MarketDefinitions.CentsPerIsk)));
    }
}
