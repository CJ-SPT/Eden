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
    BlueprintResearch? Blueprint = null
);

public sealed record ManufacturingPlan(long DurationSeconds, IReadOnlyDictionary<int, int> Materials);

public static class ManufacturingRules
{
    public static ManufacturingPlan Plan(IndustryRequest request, ManufacturingInputs inputs)
    {
        if (request.Runs <= 0)
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
        if (blueprint.Copy && request.Runs > blueprint.RunsRemaining)
        {
            throw new InvalidDataException("There are not enough blueprint copy runs remaining.");
        }
        double seconds = inputs.BaseSeconds * request.Runs * (1 - blueprint.TimeEfficiency / 100d);
        foreach (var bonus in bonuses.Where(b => !b.Additive && b.Attribute == DogmaAttributeId.ManufactureTimeMultiplier))
        {
            seconds *= 1 + bonus.Value / 100;
        }
        foreach (var skill in inputs.SkillTimeBonuses)
        {
            seconds *= 1 + skill.PercentPerLevel * skill.Level / 100;
        }
        // Activity.job_max_runs uses ceil(30 days / modified single-run time),
        // capped at MAX_RUNS_HARD_CAP. An exact 30-day boundary adds no extra run.
        if (
            !double.IsFinite(seconds)
            || seconds <= 0
            || request.Runs
                > Math.Min(
                    IndustryDefinitions.MaximumJobRuns,
                    Math.Ceiling(IndustryDefinitions.MaximumJobSeconds / (seconds / request.Runs))
                )
        )
        {
            throw new InvalidDataException("The job exceeds the manufacturing run-length limit.");
        }
        long duration = EconomicRules.ClientRound(seconds, IndustryDefinitions.ManufacturingTimePrecision);
        if (duration <= 0 || !double.IsFinite(request.QuotedSeconds) || request.QuotedSeconds != duration)
        {
            throw new InvalidDataException("Manufacturing time changed; reopen the blueprint to refresh the quote.");
        }
        var materials = IndustryMaterials.Calculate(inputs.Materials, request.Runs, blueprint.MaterialEfficiency);
        if (materials.Count == 0)
        {
            throw new InvalidDataException("Missing manufacturing materials.");
        }
        IndustryMaterials.CheckQuote(materials, request.QuotedMaterials);
        return new(duration, materials);
    }
}
