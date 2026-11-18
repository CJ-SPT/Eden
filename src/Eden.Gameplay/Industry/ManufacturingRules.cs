using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;

namespace Eden.Gameplay.Industry;

public sealed record ManufacturingMaterial(long? TypeId, long? Quantity);

public sealed record ManufacturingSkillTimeBonus(double PercentPerLevel, int Level);

public sealed record ManufacturingInputs(
    double BaseSeconds,
    int InstalledJobs,
    IReadOnlyList<IndustryCharacterBonus> Bonuses,
    IReadOnlyList<ManufacturingSkillTimeBonus> SkillTimeBonuses,
    IReadOnlyList<ManufacturingMaterial> Materials,
    BlueprintResearch? Blueprint = null,
    double FacilityTimeMultiplier = 1,
    double FacilityMaterialMultiplier = 1
);

public sealed record ManufacturingPlan(long DurationSeconds, IReadOnlyDictionary<int, int> Materials);

public static class ManufacturingRules
{
    public static ManufacturingPlan Plan(IndustryRequest request, ManufacturingInputs inputs)
    {
        var plan = Plan(request.Runs, inputs);
        if (!double.IsFinite(request.QuotedSeconds) || request.QuotedSeconds != plan.DurationSeconds)
        {
            throw new InvalidDataException("Manufacturing time changed; reopen the blueprint to refresh the quote.");
        }
        IndustryMaterials.CheckQuote(plan.Materials, request.QuotedMaterials);
        return plan;
    }

    public static ManufacturingPlan Plan(int runs, ManufacturingInputs inputs)
    {
        if (runs <= 0)
        {
            throw new InvalidDataException("Choose an available blueprint and positive run count at this station.");
        }
        var bonuses = inputs.Bonuses;
        int slots = checked(
            IndustryDefinitions.BaseManufacturingSlots
            + (int)bonuses.Where(b => b.Additive && b.Attribute == DogmaAttributeId.ManufactureSlotLimit).Sum(b => b.Value)
        );
        if (inputs.InstalledJobs >= slots)
        {
            throw new InvalidDataException("Deliver or cancel an existing job to free a manufacturing slot.");
        }
        var blueprint = inputs.Blueprint ?? new BlueprintResearch();
        if (
            blueprint.TimeEfficiency is < 0 or > IndustryDefinitions.MaximumTimeEfficiency
            || blueprint.Copy && runs > blueprint.RunsRemaining
        )
        {
            throw new InvalidDataException("Invalid blueprint efficiency or insufficient licensed copy runs.");
        }
        double seconds = inputs.BaseSeconds * runs * (1 - blueprint.TimeEfficiency / 100d) * inputs.FacilityTimeMultiplier;
        foreach (var bonus in bonuses.Where(b => !b.Additive && b.Attribute == DogmaAttributeId.ManufactureTimeMultiplier))
        {
            seconds *= 1 + bonus.Value / 100;
        }
        foreach (var skill in inputs.SkillTimeBonuses)
        {
            seconds *= 1 + skill.PercentPerLevel * skill.Level / 100;
        }
        // Activity.job_max_runs uses ceil(30 days / modified single-run time),
        // capped at MAX_RUNS_HARD_CAP. Manufacturing.job_max_runs exempts copies
        // from the time limit and uses their remaining licensed runs instead.
        if (
            !double.IsFinite(seconds)
            || seconds <= 0
            || runs
                > Math.Min(
                    IndustryDefinitions.MaximumJobRuns,
                    blueprint.Copy
                        ? IndustryDefinitions.MaximumJobRuns
                        : Math.Ceiling(IndustryDefinitions.MaximumJobSeconds / (seconds / runs))
                )
        )
        {
            throw new InvalidDataException("The job exceeds the manufacturing run-length limit.");
        }
        long duration = Math.Max(
            IndustryDefinitions.MinimumJobSeconds,
            EconomicRules.ClientRound(seconds, IndustryDefinitions.ManufacturingTimePrecision)
        );
        var materials = IndustryMaterials.Calculate(
            inputs.Materials,
            runs,
            blueprint.MaterialEfficiency,
            inputs.FacilityMaterialMultiplier
        );
        if (materials.Count == 0)
        {
            throw new InvalidDataException("Missing manufacturing materials.");
        }
        return new(duration, materials);
    }
}
