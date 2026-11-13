using Eden.Gameplay.Agents;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Mining;

namespace Eden.Gameplay.Configuration;

public static class WorldTuningRules
{
    private const double Percent = 100;
    public const decimal MinimumRate = 0.1m;
    public const decimal MaximumRate = 100m;

    public static void ValidateRate(decimal value, bool reward = false)
    {
        if (value < (reward ? 0 : MinimumRate) || value > MaximumRate)
        {
            throw new InvalidDataException($"Multiplier must be between {(reward ? 0 : MinimumRate)} and {MaximumRate}.");
        }
    }

    public static void Validate(WorldSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings.Resources);
        ArgumentNullException.ThrowIfNull(settings.Rewards);
        ArgumentNullException.ThrowIfNull(settings.Industry);
        ArgumentNullException.ThrowIfNull(settings.Structures);
        ArgumentNullException.ThrowIfNull(settings.Progression);
        if (settings.Navigation is null || settings.Navigation.AutopilotGateLandingMeters is < 0 or > (int)NavigationState.MaximumWarpRange)
        {
            throw new InvalidDataException("Autopilot gate landing distance must be between 0 and 100000 metres.");
        }
        ValidateRate(settings.Resources.OreYieldMultiplier);
        ValidateRate(settings.Resources.IceYieldMultiplier);
        ValidateRate(settings.Resources.GasYieldMultiplier);
        ValidateRate(settings.Rewards.BountyMultiplier, true);
        ValidateRate(settings.Rewards.MissionIskMultiplier, true);
        ValidateRate(settings.Rewards.MissionLpMultiplier, true);
        ValidateRate(settings.Rewards.MissionStandingMultiplier, true);
        ValidateRate(settings.Progression.KillSkillPointMultiplier, true);
        ValidateRate(settings.Progression.MiningSkillPointMultiplier, true);
        ValidateRate(settings.Industry.ManufacturingSpeed);
        ValidateRate(settings.Industry.ResearchSpeed);
        ValidateRate(settings.Industry.CopyingSpeed);
        ValidateRate(settings.Industry.InventionSpeed);
        ValidateRate(settings.Industry.ReprocessingEfficiency);
        ValidateRate(settings.Structures.AnchoringSpeed);
        ValidateRate(settings.Structures.OnliningSpeed);
        ValidateRate(settings.Structures.UnanchoringSpeed);
    }

    public static decimal Yield(ResourcePolicy policy, MiningFamily family) =>
        family switch
        {
            MiningFamily.Ore => policy.OreYieldMultiplier,
            MiningFamily.Ice => policy.IceYieldMultiplier,
            MiningFamily.Gas => policy.GasYieldMultiplier,
            _ => throw new InvalidDataException("Unsupported harvesting family."),
        };

    public static decimal Speed(IndustryPolicy policy, IndustryActivity activity) =>
        activity switch
        {
            IndustryActivity.Manufacturing => policy.ManufacturingSpeed,
            IndustryActivity.ResearchMaterial or IndustryActivity.ResearchTime => policy.ResearchSpeed,
            IndustryActivity.Copying => policy.CopyingSpeed,
            IndustryActivity.Invention => policy.InventionSpeed,
            IndustryActivity.Reaction => 1,
            _ => throw new InvalidDataException("Unsupported industry activity."),
        };

    public static long Reward(long amount, decimal multiplier, bool wholePoints = false)
    {
        ValidateRate(multiplier, true);
        if (amount < 0)
        {
            throw new InvalidDataException("Reward amounts cannot be negative.");
        }
        decimal scaled = checked(amount * multiplier);
        return checked((long)(wholePoints ? decimal.Floor(scaled) : decimal.Round(scaled, 0, MidpointRounding.ToEven)));
    }

    public static IReadOnlyList<IndustryCharacterBonus> IndustryBonuses(IndustryPolicy policy)
    {
        List<IndustryCharacterBonus> result = [];
        foreach (var activity in IndustryDefinitions.Activities)
        {
            decimal speed = Speed(policy, activity);
            ValidateRate(speed);
            if (speed != 1)
            {
                var attribute =
                    activity == IndustryActivity.Manufacturing
                        ? DogmaAttributeId.ManufactureTimeMultiplier
                        : ScienceRules.TimeAttribute(activity);
                // Native character time bonuses are percentages of duration, not speed.
                result.Add(new(null, attribute, Percent * (1 / (double)speed - 1), false));
            }
        }
        return result;
    }

    public static MissionRewards Mission(MissionRewards reward, RewardPolicy policy)
    {
        ValidateRate(policy.MissionStandingMultiplier, true);
        double Gain(double fraction) => fraction > 0 ? Math.Min(1, fraction * (double)policy.MissionStandingMultiplier) : fraction;
        return new(
            Reward(reward.IskCents, policy.MissionIskMultiplier),
            Reward(reward.LoyaltyPoints, policy.MissionLpMultiplier, true),
            Gain(reward.CorporationFraction),
            Gain(reward.FactionFraction)
        );
    }
}
