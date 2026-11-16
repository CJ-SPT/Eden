using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Exploration;

public enum SleeperResearchMaterial
{
    DefensiveSubsystemDatacore = 11496,
    PropulsionSubsystemDatacore = 20114,
    CoreSubsystemDatacore = 20115,
    OffensiveSubsystemDatacore = 20425,
    IntactThrusterSections = 30187,
    MalfunctioningThrusterSections = 30558,
    WreckedThrusterSections = 30562,
    IntactPowerCores = 30582,
    MalfunctioningPowerCores = 30586,
    WreckedPowerCores = 30588,
    IntactArmorNanobot = 30614,
    MalfunctioningArmorNanobot = 30615,
    WreckedArmorNanobot = 30618,
    IntactWeaponSubroutines = 30628,
    MalfunctioningWeaponSubroutines = 30632,
    WreckedWeaponSubroutines = 30633,
    IntactHullSection = 30752,
    MalfunctioningHullSection = 30753,
    WreckedHullSection = 30754,
    SmallIntactHullSection = 34412,
    SmallMalfunctioningHullSection = 34414,
    SmallWreckedHullSection = 34416,
}

public static class SleeperExplorationLoot
{
    private static readonly SleeperResearchMaterial[] s_datacores =
    [
        SleeperResearchMaterial.DefensiveSubsystemDatacore,
        SleeperResearchMaterial.PropulsionSubsystemDatacore,
        SleeperResearchMaterial.CoreSubsystemDatacore,
        SleeperResearchMaterial.OffensiveSubsystemDatacore,
    ];

    private static readonly SleeperResearchMaterial[] s_relics =
    [
        SleeperResearchMaterial.IntactThrusterSections,
        SleeperResearchMaterial.MalfunctioningThrusterSections,
        SleeperResearchMaterial.WreckedThrusterSections,
        SleeperResearchMaterial.IntactPowerCores,
        SleeperResearchMaterial.MalfunctioningPowerCores,
        SleeperResearchMaterial.WreckedPowerCores,
        SleeperResearchMaterial.IntactArmorNanobot,
        SleeperResearchMaterial.MalfunctioningArmorNanobot,
        SleeperResearchMaterial.WreckedArmorNanobot,
        SleeperResearchMaterial.IntactWeaponSubroutines,
        SleeperResearchMaterial.MalfunctioningWeaponSubroutines,
        SleeperResearchMaterial.WreckedWeaponSubroutines,
    ];

    private static readonly SleeperResearchMaterial[] s_hulls =
    [
        SleeperResearchMaterial.IntactHullSection,
        SleeperResearchMaterial.MalfunctioningHullSection,
        SleeperResearchMaterial.WreckedHullSection,
    ];

    private static readonly SleeperResearchMaterial[] s_smallHulls =
    [
        SleeperResearchMaterial.SmallIntactHullSection,
        SleeperResearchMaterial.SmallMalfunctioningHullSection,
        SleeperResearchMaterial.SmallWreckedHullSection,
    ];

    // Item families are native T3 inputs. Selection and quantities are Eden policy, not retail drop probabilities.
    public static IReadOnlyList<ExplorationLootStack> Roll(int seed, int wormholeClass, ExplorationKind kind, int containerType)
    {
        if (wormholeClass is < 1 or > 6 || kind is not (ExplorationKind.Data or ExplorationKind.Relic))
        {
            throw new ArgumentOutOfRangeException(nameof(wormholeClass));
        }
        var pool =
            containerType == SleeperRules.DerelictTalocanFrigate ? s_smallHulls
            : SleeperRules.IsTalocan(containerType) ? s_hulls
            : kind == ExplorationKind.Data ? s_datacores
            : s_relics;
        int rolls = kind == ExplorationKind.Data && !SleeperRules.IsTalocan(containerType) ? 2 : 1;
        List<ExplorationLootStack> loot = [];
        for (int index = 0; index < rolls; index++)
        {
            uint roll = NpcSpawnRules.Seed(index, seed);
            int quantity =
                kind == ExplorationKind.Data && !SleeperRules.IsTalocan(containerType) ? 1 + (int)(roll % (uint)wormholeClass) : 1;
            loot.Add(new((int)pool[roll % (uint)pool.Length], quantity));
        }
        return loot;
    }
}
