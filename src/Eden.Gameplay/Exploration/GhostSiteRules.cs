using Eden.Gameplay.Combat;

namespace Eden.Gameplay.Exploration;

public static class GhostSiteRules
{
    public const int ContainerGroup = 306;
    public const int Archetype = 39; // Build 3662168 Covert Research Facility signatures.
    public const int HackingEnabledAttribute = 1927;
    public const int HackingDifficultyAttribute = 1919;
    public const int FirstDungeon = 5533;
    public const int LastDungeon = 5552;
    public const int FirstCacheType = 33530;
    public const int LastCacheType = 33534;
    public const int FirstBonusType = 57436;
    public const int LastBonusType = 57441;
    public const int WormholeBand = 3;
    public const long FirstSceneryId = 8_900_000_000_000;
    public const long VisibleCountdown = 30 * TimeSpan.TicksPerSecond;
    public const long ResponseLifetime = 60 * TimeSpan.TicksPerSecond;
    public const double ExplosionRange = 10_000;

    private const int MinimumHiddenSeconds = 30;
    private const int MaximumHiddenSeconds = 180;
    private const int ResearchTools = 33577;
    private const int VillardWheel = 33539;
    private const int ElectroNeuralSignaller = 57450;
    private const int NanoRegulationGate = 57449;

    public static bool IsCache(int type) => type is >= FirstCacheType and <= LastCacheType || IsBonusCache(type);

    public static bool IsBonusCache(int type) => type is >= FirstBonusType and <= LastBonusType;

    public static int MaximumFailures(ExplorationKind? kind) => kind == ExplorationKind.Ghost ? 1 : HackingRules.MaximumFailures;

    public static long ResponseAt(long now, long siteId)
    {
        var random = new Random(unchecked((int)siteId));
        return checked(now + random.Next(MinimumHiddenSeconds, MaximumHiddenSeconds + 1) * TimeSpan.TicksPerSecond);
    }

    public static DamageVector Explosion(int band) => new(0, 6_000 + 2_000 * band, 0, 0);

    private static readonly int[][] s_blueprintPools =
    [
        [33556, 33562], // Mid-grade Ascendancy Alpha and Epsilon.
        [33543, 33521, 33701], // High-grade Beta, Wetu and Packrat.
        [33545, 33546, 33703], // High-grade Gamma and Epsilon, and Magpie.
        [33545, 33546, 33548, 33703], // High-grade Gamma, Epsilon and Omega, and Magpie.
    ];

    public static int? Blueprint(int seed, int band, int cache)
    {
        if (IsBonusCache(cache))
        {
            return null;
        }
        const int blueprintChance = 10;
        Random random = new(seed ^ FirstBonusType);
        if (random.Next(blueprintChance) != 0)
        {
            return null;
        }
        var pool = s_blueprintPools[band];
        return pool[random.Next(pool.Length)];
    }

    public static IReadOnlyList<ExplorationLootStack> Loot(int seed, int band, bool bonus)
    {
        // Published source categories. Eden chooses the quantities and chances.
        if (bonus)
        {
            return [new(ElectroNeuralSignaller, 1)];
        }
        Random random = new(seed);
        List<ExplorationLootStack> loot = [new(ResearchTools, random.Next(2, 6 + band))];
        if (random.Next(8) == 0)
        {
            loot.Add(new(VillardWheel, 1));
        }
        if (random.Next(10 - band) == 0)
        {
            loot.Add(new(random.Next(2) == 0 ? ElectroNeuralSignaller : NanoRegulationGate, 1));
        }
        return loot;
    }
}
