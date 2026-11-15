using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Skills;

namespace Eden.Server.Administration;

internal sealed record ProgressionPreview(decimal RewardValueIsk, long SkillPoints, decimal BaseValueMultiplier)
{
    internal static ProgressionPreview Combat(WorldSettings settings, decimal baseBountyIsk)
    {
        if (baseBountyIsk < 0)
        {
            throw new InvalidDataException("Example bounty cannot be negative.");
        }
        long baseCents = checked((long)decimal.Round(baseBountyIsk * MarketDefinitions.CentsPerIsk, 0, MidpointRounding.ToEven));
        long cents = WorldTuningRules.Reward(baseCents, settings.Rewards.BountyMultiplier);
        var progression = settings.Progression;
        long points = NpcSkillPointRewards.FromBounty(cents, progression.KillSkillPointMultiplier, progression.KillSkillPointPercent);
        decimal scale = settings.Rewards.BountyMultiplier * progression.KillSkillPointMultiplier;
        return new(cents / (decimal)MarketDefinitions.CentsPerIsk, points, scale);
    }

    internal static ProgressionPreview Mining(WorldSettings settings, decimal baseReferenceIsk, MiningFamily family)
    {
        decimal yield = WorldTuningRules.Yield(settings.Resources, family);
        WorldTuningRules.ValidateRate(yield);
        if (baseReferenceIsk < 0)
        {
            throw new InvalidDataException("Example harvest value cannot be negative.");
        }
        decimal value = baseReferenceIsk * yield;
        var progression = settings.Progression;
        long points = ActivitySkillPointRewards.FromValue(
            value,
            1,
            progression.MiningSkillPointMultiplier,
            progression.MiningIskPerSkillPoint
        );
        return new(value, points, yield * progression.MiningSkillPointMultiplier);
    }
}
