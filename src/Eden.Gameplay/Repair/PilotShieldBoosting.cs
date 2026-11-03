using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Repair;

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
    CapitalOperation = 21802,
}

public static class ShieldBoostDefinitions
{
    public const int Group = 40;
}
