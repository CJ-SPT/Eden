namespace Eden.Gameplay.Thermal;

public sealed record FittedModuleCondition(ModuleCondition Condition, bool Online, int OverloadEffect);

public interface IPilotModuleConditions
{
    IReadOnlyList<FittedModuleCondition> Read(long pilot, long ship);
}
