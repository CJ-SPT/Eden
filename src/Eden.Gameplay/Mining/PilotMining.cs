using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Mining;

public sealed record MiningLaser(
    long ItemId,
    int TypeId,
    double Range,
    double DurationMilliseconds,
    double Volume,
    double CapacitorNeed,
    double CriticalChance,
    double CriticalBonusYield
);

public sealed record MiningStart(MiningLaser Laser, double Capacitor);

public sealed record MiningYield(InventoryChange? Cargo, int TypeId, int Extracted, int CriticalBonus, bool Depleted, bool CargoFull);

public interface IPilotMining
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now);
    MiningYield Collect(long pilotId, long moduleId, long asteroidId, double volume, double criticalBonusYield);
}

public enum MiningSkill
{
    Mining = 3386,
    Astrogeology = 3410,
    Precision = 90727,
    Exploitation = 90728,
    Frigate = 32918,
}

public enum MiningLaserType
{
    MinerII = 482,
    MinerI = 483,
    Civilian = 3651,
}

public static class MiningDefinitions
{
    public const int CorvetteGroup = FleetDefinitions.CorvetteGroup;
    public const double YieldBonusPerSkillLevel = 0.05;
    public const double CriticalChanceBonusPerSkillLevel = 0.10;
    public const double CriticalYieldBonusPerSkillLevel = 0.05;
}
