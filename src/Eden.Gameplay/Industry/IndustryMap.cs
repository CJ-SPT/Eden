namespace Eden.Gameplay.Industry;

public readonly record struct IndustryMapJobCount(int SystemId, int Count);

public readonly record struct IndustryMapCostModifier(int SystemId, decimal Index);

public interface IIndustryMap
{
    IReadOnlyList<IndustryMapJobCount> JobsStarted(IndustryActivity? activity);
    IReadOnlyList<IndustryMapCostModifier> CostModifiers(IndustryActivity activity);
}
