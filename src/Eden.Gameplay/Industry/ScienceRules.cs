using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;

namespace Eden.Gameplay.Industry;

public sealed record BlueprintResearch(int MaterialEfficiency = 0, int TimeEfficiency = 0, int RunsRemaining = -1)
{
    public bool Copy => RunsRemaining >= 0;
}

public sealed record InventionModifiers(double Probability = 1, int MaterialEfficiency = 0, int TimeEfficiency = 0, int Runs = 0);

public sealed record ScienceInputs(
    BlueprintResearch Blueprint,
    double BaseSeconds,
    int MaximumCopyRuns,
    int InstalledJobs,
    IReadOnlyList<IndustryCharacterBonus> Bonuses,
    IReadOnlyList<ManufacturingMaterial> Materials,
    double BaseProbability = 1,
    double SkillProbabilityBonus = 0,
    int InventionOutputRuns = 1,
    InventionModifiers? Decryptor = null
);

public sealed record SciencePlan(
    long DurationSeconds,
    IReadOnlyDictionary<int, int> Materials,
    double CostMultiplier,
    int MaterialEfficiency,
    int TimeEfficiency,
    int OutputRuns,
    double Probability
);

public static class ScienceRules
{
    public const double SkillProbability = 1 / 30d;
    public const double ReducedSkillProbability = 1 / 40d;

    // Build 3662168 industry.const.RESEARCH_TIMES: incremental, not cumulative.
    private static readonly double[] s_researchTimes =
    [
        1,
        29 / 21d,
        23 / 7d,
        39 / 5d,
        278 / 15d,
        928 / 21d,
        2200 / 21d,
        5251 / 21d,
        4163 / 7d,
        29660 / 21d,
    ];

    public static DogmaAttributeId TimeAttribute(IndustryActivity activity) =>
        activity switch
        {
            IndustryActivity.ResearchMaterial => DogmaAttributeId.MineralNeedResearchSpeed,
            IndustryActivity.ResearchTime => DogmaAttributeId.ManufacturingTimeResearchSpeed,
            IndustryActivity.Copying => DogmaAttributeId.CopySpeedPercent,
            IndustryActivity.Invention => DogmaAttributeId.InventionResearchSpeed,
            _ => throw new InvalidDataException("Unsupported science activity."),
        };

    public static SciencePlan Plan(IndustryRequest request, ScienceInputs inputs)
    {
        var timeAttribute = TimeAttribute(request.Activity);
        var blueprint = inputs.Blueprint;
        bool invention = request.Activity == IndustryActivity.Invention;
        if (request.Runs <= 0 || invention != blueprint.Copy || invention && request.Runs > blueprint.RunsRemaining)
        {
            throw new InvalidDataException("Research and copying require an original; invention requires enough blueprint copy runs.");
        }
        int slots = checked(
            IndustryDefinitions.BaseLaboratorySlots
            + (int)inputs.Bonuses.Where(b => b.Attribute == DogmaAttributeId.MaximumLaboratorySlots && b.Additive).Sum(b => b.Value)
        );
        if (inputs.InstalledJobs >= slots)
        {
            throw new InvalidDataException("Deliver or cancel an existing science job to free a laboratory slot.");
        }
        double multiplier = 1;
        foreach (var bonus in inputs.Bonuses.Where(b => b.Attribute == timeAttribute && !b.Additive))
        {
            multiplier *= 1 + bonus.Value / 100;
        }
        int maximumRuns = request.Activity is IndustryActivity.Copying or IndustryActivity.Invention
            ? IndustryDefinitions.MaximumScienceRuns
            : IndustryDefinitions.MaximumJobRuns;
        double maximumByTime = Math.Ceiling(IndustryDefinitions.MaximumJobSeconds / inputs.BaseSeconds / multiplier);
        if (
            !double.IsFinite(multiplier)
            || multiplier <= 0
            || inputs.BaseSeconds <= 0
            || !double.IsFinite(inputs.BaseSeconds)
            || request.Runs > Math.Min(maximumRuns, maximumByTime)
        )
        {
            throw new InvalidDataException("The science job exceeds the supported run limit.");
        }
        int materialEfficiency = blueprint.MaterialEfficiency;
        int timeEfficiency = blueprint.TimeEfficiency;
        double units = request.Runs;
        int outputRuns = 0;
        if (request.Activity is IndustryActivity.ResearchMaterial or IndustryActivity.ResearchTime)
        {
            int current =
                request.Activity == IndustryActivity.ResearchMaterial
                    ? materialEfficiency
                    : timeEfficiency / IndustryDefinitions.TimeEfficiencyStep;
            if (current < 0 || current + request.Runs > s_researchTimes.Length || request.LicensedRuns != 0)
            {
                throw new InvalidDataException("The job exceeds the blueprint's research limit.");
            }
            units = s_researchTimes.Skip(current).Take(request.Runs).Sum();
            if (request.Activity == IndustryActivity.ResearchMaterial)
            {
                materialEfficiency += request.Runs;
            }
            else
            {
                timeEfficiency += request.Runs * IndustryDefinitions.TimeEfficiencyStep;
            }
        }
        else if (request.Activity == IndustryActivity.Copying)
        {
            if (request.LicensedRuns <= 0 || request.LicensedRuns > inputs.MaximumCopyRuns)
            {
                throw new InvalidDataException("Choose a positive copy run count within the blueprint's production limit.");
            }
            units *= request.LicensedRuns;
            outputRuns = request.LicensedRuns;
        }
        else
        {
            if (request.LicensedRuns != 0)
            {
                throw new InvalidDataException("Invention output runs are determined by the catalog and decryptor.");
            }
            materialEfficiency = IndustryDefinitions.InventionMaterialEfficiency + (inputs.Decryptor?.MaterialEfficiency ?? 0);
            timeEfficiency = IndustryDefinitions.InventionTimeEfficiency + (inputs.Decryptor?.TimeEfficiency ?? 0);
            outputRuns = checked(inputs.InventionOutputRuns + (inputs.Decryptor?.Runs ?? 0));
            if (outputRuns <= 0)
            {
                throw new InvalidDataException("Invalid invention output runs.");
            }
        }
        long duration = Math.Max(
            IndustryDefinitions.MinimumJobSeconds,
            EconomicRules.ClientRound(inputs.BaseSeconds * units * multiplier, 0)
        );
        if (duration <= 0 || request.QuotedSeconds != duration)
        {
            throw new InvalidDataException("Science time changed; reopen the blueprint to refresh the quote.");
        }
        int materialRuns = request.Activity == IndustryActivity.Copying ? checked(request.Runs * request.LicensedRuns) : request.Runs;
        var materials = IndustryMaterials.Calculate(inputs.Materials, materialRuns);
        IndustryMaterials.CheckQuote(materials, request.QuotedMaterials);
        double probability = invention
            ? Math.Clamp(inputs.BaseProbability * (1 + inputs.SkillProbabilityBonus) * (inputs.Decryptor?.Probability ?? 1), 0, 1)
            : 1;
        if (!double.IsFinite(probability))
        {
            throw new InvalidDataException("Invalid invention probability.");
        }
        return new(
            duration,
            materials,
            units * IndustryDefinitions.ScienceCostPercentage,
            materialEfficiency,
            timeEfficiency,
            outputRuns,
            probability
        );
    }

    public static int SuccessfulRuns(double probability, IReadOnlyList<double> rolls)
    {
        if (!double.IsFinite(probability) || probability is < 0 or > 1 || rolls.Any(r => !double.IsFinite(r) || r is < 0 or >= 1))
        {
            throw new InvalidDataException("Invalid invention outcome inputs.");
        }
        return rolls.Count(r => r < probability);
    }
}

public static class IndustryMaterials
{
    public static IReadOnlyDictionary<int, int> Calculate(IReadOnlyList<ManufacturingMaterial> recipe, int runs, int efficiency = 0)
    {
        if (runs <= 0 || efficiency is < 0 or > IndustryDefinitions.MaximumMaterialEfficiency)
        {
            throw new InvalidDataException("Invalid blueprint material inputs.");
        }
        Dictionary<int, int> materials = [];
        foreach (var material in recipe)
        {
            if (material.TypeId is not > 0 or > int.MaxValue || material.Quantity is not > 0 || materials.ContainsKey((int)material.TypeId))
            {
                throw new InvalidDataException("Invalid industry material recipe.");
            }
            double quantity = material.Quantity.Value * (1 - efficiency / 100d) * runs;
            int rounded = checked((int)Math.Ceiling(EconomicRules.ClientRound(quantity, 2) / 100d));
            materials.Add((int)material.TypeId, Math.Max(runs, rounded));
        }
        return materials;
    }

    public static void CheckQuote(IReadOnlyDictionary<int, int> materials, IReadOnlyDictionary<int, int> quoted)
    {
        if (materials.Count != quoted.Count || materials.Any(m => quoted.GetValueOrDefault(m.Key) != m.Value))
        {
            throw new InvalidDataException("Industry materials changed; reopen the blueprint to refresh the quote.");
        }
    }
}
