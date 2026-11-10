using Eden.Gameplay.Configuration;

namespace Eden.Gameplay.Reprocessing;

public sealed record ReprocessingMaterial(int TypeId, long Quantity);

public sealed record ReprocessingInput(
    long ItemId,
    int TypeId,
    int Quantity,
    int PortionSize,
    double StationEfficiency,
    IReadOnlyList<double> SkillMultipliers,
    IReadOnlyList<ReprocessingMaterial> Materials,
    decimal EfficiencyMultiplier = 1
);

public static class ReprocessingRules
{
    public static ReprocessingQuote Quote(ReprocessingInput input)
    {
        WorldTuningRules.ValidateRate(input.EfficiencyMultiplier);
        if (input.PortionSize <= 0 || input.Materials.Count == 0)
        {
            return new(input.ItemId, input.TypeId, 0, input.Quantity, 0, input.StationEfficiency, []);
        }
        int portions = input.Quantity / input.PortionSize;
        int left = input.Quantity % input.PortionSize;
        double efficiency = Math.Clamp(
            input.SkillMultipliers.Aggregate(input.StationEfficiency, (value, bonus) => value * bonus) * (double)input.EfficiencyMultiplier,
            0,
            1
        );
        List<RecoveredMaterial> recovered = [];
        if (portions > 0)
        {
            foreach (var material in input.Materials)
            {
                if (material.TypeId <= 0 || material.Quantity <= 0)
                {
                    throw new InvalidDataException("The catalog contains an invalid reprocessing output.");
                }
                long total = checked(material.Quantity * portions);
                int quantity = checked((int)Math.Floor(total * efficiency));
                recovered.Add(new(material.TypeId, quantity, total - quantity));
            }
        }
        return new(input.ItemId, input.TypeId, input.Quantity - left, left, portions, efficiency, recovered);
    }
}
