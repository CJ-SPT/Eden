using Eden.Gameplay.Ammunition;
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
    double ResidueMultiplier = 1,
    MiningFamily Family = MiningFamily.Ore
)
{
    public DogmaEffect Effect => Family == MiningFamily.Gas ? DogmaEffect.MiningClouds : DogmaEffect.MiningLaser;
}

public sealed record MiningStart(MiningLaser Laser, double Capacitor);

public sealed record MiningRolls(
    double Yield,
    double Critical,
    double CriticalYield,
    double Residue,
    double ResidueYield,
    double Crystal = .5
);

public sealed record MiningCollection(double Fraction, bool Completed, MiningRolls Rolls);

public sealed record MiningYield(
    InventoryChange? Cargo,
    int TypeId,
    int Extracted,
    int CriticalBonus,
    int Residue,
    bool Depleted,
    bool CargoFull,
    Magazine? Crystal = null,
    MiningHoldFill HoldFill = default
);

public interface IPilotMining
{
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    MiningStart Begin(long pilotId, long moduleId, long asteroidId, long now, DogmaEffect? effect = null);
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
    GasHarvesting = 25544,
    IceHarvesting = 16281,
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
    public const int MercoxitGroup = 468;
    public const int GasCloudGroup = 711;
    public const int GasCloudCategory = 2;
    public const int IceGroup = 465;
    public const double IceBlockVolume = 1000;

    public static bool IsResource(int category, int group) =>
        category == AsteroidDefinitions.Category || category == GasCloudCategory && group == GasCloudGroup;

    public static MiningFamily Family(int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        group is InventoryDefinitions.GasScoopGroup or InventoryDefinitions.GasHarvesterGroup ? MiningFamily.Gas
        : InventoryDefinitions.RequiredSkills.Any(p => attributes.GetValueOrDefault(p.Skill) == (int)MiningSkill.IceHarvesting)
            ? MiningFamily.Ice
        : MiningFamily.Ore;

    public static bool OwnsYieldSkill(int skill) => skill is (int)MiningSkill.Mining or (int)MiningSkill.Astrogeology;

    public static bool IsDeepCore(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        InventoryDefinitions.RequiredSkills.Any(pair => attributes.GetValueOrDefault(pair.Skill) == DeepCoreMiningSkill);

    public static bool IsOreStripMiner(IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        InventoryDefinitions.RequiredSkills.Any(pair => attributes.GetValueOrDefault(pair.Skill) == (int)MiningSkill.Mining);

    public static bool MatchesOre(int group, IReadOnlyDictionary<DogmaAttributeId, double> attributes) =>
        group == MercoxitGroup ? IsDeepCore(attributes) : Enum.IsDefined((MiningResourceGroup)group);

    public const double YieldBonusPerSkillLevel = 0.05;
    public const double CriticalChanceBonusPerSkillLevel = 0.10;
    public const double CriticalYieldBonusPerSkillLevel = 0.05;

    // Effect 391 selects Mining; effects 12755/12757 also select Ice Harvesting.
    public static IReadOnlyList<MiningSkillModifier> SkillModifiers { get; } =
    [
        new(MiningSkill.Mining, DogmaAttributeId.MiningAmount, YieldBonusPerSkillLevel),
        new(MiningSkill.Astrogeology, DogmaAttributeId.MiningAmount, YieldBonusPerSkillLevel),
        new(MiningSkill.Precision, DogmaAttributeId.MiningCriticalChance, CriticalChanceBonusPerSkillLevel),
        new(MiningSkill.Exploitation, DogmaAttributeId.MiningCriticalBonusYield, CriticalYieldBonusPerSkillLevel),
    ];
}
