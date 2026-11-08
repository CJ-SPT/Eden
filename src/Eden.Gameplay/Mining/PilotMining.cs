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
    double CriticalBonusYield,
    double ResidueChance = 0,
    double ResidueMultiplier = 1
);

public sealed record MiningStart(MiningLaser Laser, double Capacitor);

public sealed record MiningRolls(double Yield, double Critical, double CriticalYield, double Residue, double ResidueYield);

public sealed record MiningCollection(double Fraction, bool Completed, MiningRolls Rolls);

public sealed record MiningYield(
    InventoryChange? Cargo,
    int TypeId,
    int Extracted,
    int CriticalBonus,
    int Residue,
    bool Depleted,
    bool CargoFull
);

public interface IPilotMining
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now);
    MiningYield Collect(long pilotId, long moduleId, long asteroidId, MiningCollection collection);
    void Stop(long pilotId, long moduleId, long expectedCompletesAt);
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

public sealed record MiningSkillModifier(MiningSkill Skill, DogmaAttributeId Attribute, double PerLevel);

public static class MiningDefinitions
{
    public const int DeepCoreMiningSkill = 11395;

    public static bool OwnsYieldSkill(int skill) => skill is (int)MiningSkill.Mining or (int)MiningSkill.Astrogeology;

    public static bool IsOrdinaryStripMiner(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        InventoryDefinitions.RequiredSkills.Any(pair => attributes.GetValueOrDefault(pair.Skill) == (int)MiningSkill.Mining)
        && InventoryDefinitions.RequiredSkills.All(pair => attributes.GetValueOrDefault(pair.Skill) != DeepCoreMiningSkill);

    public const double YieldBonusPerSkillLevel = 0.05;
    public const double CriticalChanceBonusPerSkillLevel = 0.10;
    public const double CriticalYieldBonusPerSkillLevel = 0.05;

    // Pinned effects 391/12755/12757 select equipment requiring Mining.
    public static IReadOnlyList<MiningSkillModifier> SkillModifiers { get; } =
    [
        new(MiningSkill.Mining, DogmaAttributeId.MiningAmount, YieldBonusPerSkillLevel),
        new(MiningSkill.Astrogeology, DogmaAttributeId.MiningAmount, YieldBonusPerSkillLevel),
        new(MiningSkill.Precision, DogmaAttributeId.MiningCriticalChance, CriticalChanceBonusPerSkillLevel),
        new(MiningSkill.Exploitation, DogmaAttributeId.MiningCriticalBonusYield, CriticalYieldBonusPerSkillLevel),
    ];
}
