using Eden.Protocol.Docking;

namespace Eden.Protocol.Mining;

public sealed record MiningLaser(long ItemId, int TypeId, double Range, double DurationMilliseconds,
    double Volume, double CapacitorNeed, double CriticalChance, double CriticalBonusYield);

public sealed record MiningStart(MiningLaser Laser, double Capacitor);
public sealed record MiningYield(InventoryChange? Cargo, int TypeId, int Extracted, int CriticalBonus, bool Depleted, bool CargoFull);

public interface IPilotMining
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    double Capacitor(long pilotId, long now);
    MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now);
    MiningYield Collect(long pilotId, long moduleId, long asteroidId, double volume, double criticalBonusYield);
}

public enum MiningSkill
{
    Mining = 3386,
    Astrogeology = 3410,
    Precision = 90727,
    Exploitation = 90728,
}

public static class MiningDefinitions
{
    public const string EffectName = "miningLaser";
    public const int CorvetteGroup = 237;
    public const double YieldBonusPerSkillLevel = 0.05;
    public const double CriticalChanceBonusPerSkillLevel = 0.10;
    public const double CriticalYieldBonusPerSkillLevel = 0.05;
    // godma.CreateChargedAttribute divides recharge time by five; GetChargeValue
    // evaluates this curve using elapsed milliseconds (build 3662168).
    private const double RechargeTimeScale = 5;

    public static double Recharged(double charge, double capacity, double rechargeMilliseconds, double elapsedMilliseconds)
    {
        if (capacity <= 0) { return 0; }
        if (rechargeMilliseconds <= 0) { return capacity; }
        double fraction = Math.Clamp(charge / capacity, 0, 1);
        double root = 1 + (Math.Sqrt(fraction) - 1) * Math.Exp(-Math.Max(0, elapsedMilliseconds) * RechargeTimeScale / rechargeMilliseconds);
        return capacity * root * root;
    }
}
