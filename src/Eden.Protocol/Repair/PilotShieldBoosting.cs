using Eden.Protocol.Combat;
using Eden.Protocol.Docking;

namespace Eden.Protocol.Repair;

public sealed record ShieldBooster(long ItemId, int TypeId, double DurationMilliseconds, double ShieldAmount, double CapacitorNeed);
public sealed record ShieldBoost(ShieldBooster Booster, ShipHealth Health, double Capacitor, double Restored);

public interface IPilotShieldBoosting
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    ShieldBoost Boost(long pilotId, long moduleId, long now);
}

public enum ShieldBoosterType
{
    SmallI = 399,
    Civilian = 10039,
}

public enum ShieldBoostSkill
{
    Compensation = 21059,
}

public static class ShieldBoostDefinitions
{
    public const int Group = 40;
    public const string EffectName = "shieldBoosting";
    public const string EffectGraphic = "effects.ShieldBoosting";
}
