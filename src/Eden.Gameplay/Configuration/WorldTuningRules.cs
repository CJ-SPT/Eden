using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Mining;
using Eden.Gameplay.Planetary;
using Eden.Gameplay.Skills;

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
        ArgumentNullException.ThrowIfNull(settings.PlanetaryIndustry);
        ArgumentNullException.ThrowIfNull(settings.Progression);
        ArgumentNullException.ThrowIfNull(settings.ShipFitting);
        if (settings.Navigation is null || settings.Navigation.AutopilotGateLandingMeters is < 0 or > (int)NavigationState.MaximumWarpRange)
        {
            throw new InvalidDataException("Autopilot gate landing distance must be between 0 and 100000 metres.");
        }
        ValidateRate(settings.Navigation.WarpSpeedMultiplier);
        ValidateRate(settings.Navigation.AlignSpeedMultiplier);
        ValidateRate(settings.Resources.OreYieldMultiplier);
        ValidateRate(settings.Resources.IceYieldMultiplier);
        ValidateRate(settings.Resources.GasYieldMultiplier);
        ValidateRate(settings.MiningHolds.GeneralMultiplier);
        ValidateRate(settings.MiningHolds.IceMultiplier);
        ValidateRate(settings.MiningHolds.GasMultiplier);
        ValidateRate(settings.ShipFitting.CpuMultiplier);
        ValidateRate(settings.ShipFitting.PowerGridMultiplier);
        ValidateRate(settings.ShipFitting.CalibrationMultiplier);
        ValidateRate(settings.ShipFitting.DroneBandwidthMultiplier);
        ValidateRate(settings.Rewards.BountyMultiplier, true);
        ValidateRate(settings.Rewards.MissionIskMultiplier, true);
        ValidateRate(settings.Rewards.MissionLpMultiplier, true);
        ValidateRate(settings.Rewards.MissionStandingMultiplier, true);
        ValidateRate(settings.Progression.KillSkillPointMultiplier, true);
        ValidateRate(settings.Progression.MiningSkillPointMultiplier, true);
        NpcSkillPointRewards.ValidatePercent(settings.Progression.KillSkillPointPercent);
        ActivitySkillPointRewards.ValidateIskPerSkillPoint(settings.Progression.MiningIskPerSkillPoint);
        ValidateRate(settings.Industry.ManufacturingSpeed);
        ValidateRate(settings.Industry.ResearchSpeed);
        ValidateRate(settings.Industry.CopyingSpeed);
        ValidateRate(settings.Industry.InventionSpeed);
        ValidateRate(settings.Industry.ReprocessingEfficiency);
        ValidateRate(settings.PlanetaryIndustry.ExtractionYieldMultiplier);
        ValidateRate(settings.PlanetaryIndustry.FactoryOutputMultiplier);
        ValidateRate(settings.PlanetaryIndustry.CpuMultiplier);
        ValidateRate(settings.PlanetaryIndustry.PowergridMultiplier);
        ValidateRate(settings.PlanetaryIndustry.StorageCapacityMultiplier);
        ValidateRate(settings.PlanetaryIndustry.FactorySpeedMultiplier);
        PlanetaryRules.ValidateMaximumPlanets(settings.PlanetaryIndustry.MaximumPlanets);
        PlanetaryRules.CustomsTaxRate(settings.PlanetaryIndustry);
        ValidateRate(settings.Structures.AnchoringSpeed);
        ValidateRate(settings.Structures.OnliningSpeed);
        ValidateRate(settings.Structures.UnanchoringSpeed);
        if (settings.Structures.MinimumCorporationAgeDays < 0)
        {
            throw new InvalidDataException("Minimum corporation age for anchoring must be zero or more days.");
        }
    }

    public static IEnumerable<KeyValuePair<DogmaAttributeId, double>> TravelMultipliers(NavigationPolicy policy)
    {
        if (policy.WarpSpeedMultiplier != 1)
        {
            yield return new(DogmaAttributeId.WarpSpeedMultiplier, (double)policy.WarpSpeedMultiplier);
        }
        if (policy.AlignSpeedMultiplier != 1)
        {
            // Inertia time is mass times agility; divide agility to make higher settings faster.
            yield return new(DogmaAttributeId.Agility, 1 / (double)policy.AlignSpeedMultiplier);
        }
    }

    public static void ScaleTravel(NavigationPolicy policy, Dictionary<DogmaAttributeId, double> attributes) =>
        ScalePositiveAttributes(attributes, TravelMultipliers(policy));

    public static IEnumerable<KeyValuePair<DogmaAttributeId, double>> MiningHoldMultipliers(MiningHoldPolicy policy)
    {
        if (policy.GeneralMultiplier != 1)
        {
            yield return new(DogmaAttributeId.GeneralMiningHoldCapacity, (double)policy.GeneralMultiplier);
        }
        if (policy.IceMultiplier != 1)
        {
            yield return new(DogmaAttributeId.SpecialIceHoldCapacity, (double)policy.IceMultiplier);
        }
        if (policy.GasMultiplier != 1)
        {
            yield return new(DogmaAttributeId.SpecialGasHoldCapacity, (double)policy.GasMultiplier);
        }
    }

    public static void ScaleMiningHolds(MiningHoldPolicy policy, Dictionary<DogmaAttributeId, double> attributes) =>
        ScalePositiveAttributes(attributes, MiningHoldMultipliers(policy));

    public static IEnumerable<KeyValuePair<DogmaAttributeId, double>> FittingMultipliers(ShipFittingPolicy policy)
    {
        if (policy.CpuMultiplier != 1)
        {
            yield return new(DogmaAttributeId.CpuOutput, (double)policy.CpuMultiplier);
        }
        if (policy.PowerGridMultiplier != 1)
        {
            yield return new(DogmaAttributeId.PowerOutput, (double)policy.PowerGridMultiplier);
        }
        if (policy.CalibrationMultiplier != 1)
        {
            yield return new(DogmaAttributeId.CalibrationCapacity, (double)policy.CalibrationMultiplier);
        }
        if (policy.DroneBandwidthMultiplier != 1)
        {
            yield return new(DogmaAttributeId.DroneBandwidth, (double)policy.DroneBandwidthMultiplier);
        }
    }

    public static void ScaleFitting(ShipFittingPolicy policy, Dictionary<DogmaAttributeId, double> attributes) =>
        ScalePositiveAttributes(attributes, FittingMultipliers(policy));

    public static double ScaleDroneBandwidth(ShipFittingPolicy policy, double bandwidth) =>
        bandwidth * (double)policy.DroneBandwidthMultiplier;

    private static void ScalePositiveAttributes(
        Dictionary<DogmaAttributeId, double> attributes,
        IEnumerable<KeyValuePair<DogmaAttributeId, double>> multipliers
    )
    {
        foreach (var (attribute, multiplier) in multipliers)
        {
            if (attributes.TryGetValue(attribute, out double capacity) && capacity > 0)
            {
                attributes[attribute] = capacity * multiplier;
            }
        }
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
