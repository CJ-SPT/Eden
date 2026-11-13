using Eden.Gameplay.Dogma;
using Eden.Gameplay.Economy;

namespace Eden.Gameplay.Industry;

public static class ReactionRules
{
    public static ManufacturingPlan Plan(
        IndustryRequest request,
        long baseSeconds,
        int activeJobs,
        IReadOnlyList<IndustryCharacterBonus> bonuses,
        IReadOnlyList<ManufacturingMaterial> ingredients,
        double facilityTime
    )
    {
        int slots = checked(1 + (int)bonuses.Where(b => b.Additive && b.Attribute == DogmaAttributeId.ReactionSlotLimit).Sum(b => b.Value));
        if (activeJobs >= slots)
        {
            throw new InvalidDataException("Deliver or cancel an existing reaction to free a reaction slot.");
        }
        double secondsPerRun = baseSeconds * facilityTime;
        foreach (var bonus in bonuses.Where(b => !b.Additive && b.Attribute == DogmaAttributeId.ReactionTimeMultiplier))
        {
            secondsPerRun *= 1 + bonus.Value / 100;
        }
        if (
            !double.IsFinite(secondsPerRun)
            || secondsPerRun <= 0
            || request.Runs < 1
            || request.Runs
                > Math.Min(IndustryDefinitions.MaximumJobRuns, Math.Ceiling(IndustryDefinitions.MaximumJobSeconds / secondsPerRun))
        )
        {
            throw new InvalidDataException("The job exceeds the reaction run-length limit.");
        }
        long seconds = Math.Max(IndustryDefinitions.MinimumJobSeconds, EconomicRules.ClientRound(secondsPerRun * request.Runs, 0));
        if (seconds != request.QuotedSeconds)
        {
            throw new InvalidDataException("Reaction time changed; reopen the formula to refresh the quote.");
        }
        var materials = IndustryMaterials.Calculate(ingredients, request.Runs);
        IndustryMaterials.CheckQuote(materials, request.QuotedMaterials);
        return new(seconds, materials);
    }
}
