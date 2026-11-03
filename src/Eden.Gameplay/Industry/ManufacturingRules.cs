namespace Eden.Gameplay.Industry;

public sealed record ManufacturingMaterial(long? TypeId, long? Quantity);

public sealed record ManufacturingSkillTimeBonus(double PercentPerLevel, int Level);

public sealed record ManufacturingInputs(
    double BaseSeconds,
    int InstalledJobs,
    IReadOnlyList<IndustryCharacterBonus> Bonuses,
    IReadOnlyList<ManufacturingSkillTimeBonus> SkillTimeBonuses,
    IReadOnlyList<ManufacturingMaterial> Materials
);

public sealed record ManufacturingPlan(long DurationSeconds, IReadOnlyDictionary<int, int> Materials);

public static class ManufacturingRules
{
    public static ManufacturingPlan Plan(ManufacturingRequest request, ManufacturingInputs inputs)
    {
        if (request.Runs <= 0)
        {
            throw new InvalidDataException("Choose an available blueprint and positive run count at this station.");
        }
        var bonuses = inputs.Bonuses;
        int slots = checked(IndustryDefinitions.BaseManufacturingSlots + (int)bonuses.Where(b => b.Additive).Sum(b => b.Value));
        if (inputs.InstalledJobs >= slots)
        {
            throw new InvalidDataException("Deliver or cancel an existing job to free a manufacturing slot.");
        }
        double seconds = inputs.BaseSeconds * (double)request.Runs;
        foreach (var bonus in bonuses.Where(b => !b.Additive))
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
        long duration = checked((long)Math.Round(seconds, IndustryDefinitions.ManufacturingTimePrecision, MidpointRounding.ToEven));
        if (duration <= 0 || !double.IsFinite(request.QuotedSeconds) || request.QuotedSeconds != duration)
        {
            throw new InvalidDataException("Manufacturing time changed; reopen the blueprint to refresh the quote.");
        }
        Dictionary<int, int> materials = [];
        foreach (var material in inputs.Materials)
        {
            if (
                material.TypeId is not > 0 or > int.MaxValue
                || material.Quantity is not > 0
                || materials.ContainsKey((int)material.TypeId.Value)
            )
            {
                throw new InvalidDataException("Invalid manufacturing material recipe.");
            }
            materials.Add((int)material.TypeId.Value, checked((int)(material.Quantity.Value * request.Runs)));
        }
        if (
            materials.Count == 0
            || materials.Count != request.QuotedMaterials.Count
            || materials.Any(m => request.QuotedMaterials.GetValueOrDefault(m.Key) != m.Value)
        )
        {
            throw new InvalidDataException("Manufacturing materials changed; reopen the blueprint to refresh the quote.");
        }
        return new(duration, materials);
    }
}
