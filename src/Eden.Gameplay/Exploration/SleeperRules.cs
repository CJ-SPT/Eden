namespace Eden.Gameplay.Exploration;

public enum WormholeSiteKind
{
    Combat,
    Data,
    Relic,
    Gas,
}

public enum SleeperBlueLootType
{
    NeuralNetworkAnalyzer = 30744,
    SleeperDataLibrary = 30745,
    AncientCoordinatesDatabase = 30746,
    SleeperDroneAiNexus = 30747,
}

public enum SleeperWaveTrigger
{
    Defeat,
    HackStarted,
    HackFailed,
    HackResolved,
}

public enum SleeperSpawnCondition
{
    Always,
    Talocan,
    Aggression,
}

public sealed record SleeperSpawnContext(bool Talocan, bool Aggression);

public static class SleeperRules
{
    // Build 3662168 labels the native Sleeper dungeon faction "Unknown".
    public const int FactionId = 500021;
    public const int SystemOwner = 1;
    public const int GuardedSiteSlot = -3;
    public const int ConditionalNpcSlot = 500;
    public const int RelicContainerGroup = 306;
    public const int AbandonedTalocanBattleship = 30528;
    public const int DesertedTalocanCruiser = 30529;
    public const int DerelictTalocanFrigate = 30530;
    public const long SiteLifetime = 3 * TimeSpan.TicksPerDay;
    private const int MaximumConditionalHackCount = 3;

    // Published reports do not provide a Talocan probability. Eden uses one stable roll per site.
    public static bool HasTalocan(long siteId) =>
        Eden.Gameplay.Combat.NpcSpawnRules.Seed(0, siteId) % AnomalyEncounterRules.OptionalSpawnOutcomes == 0;

    public static int HackThreshold(long siteId) =>
        1 + (int)(Eden.Gameplay.Combat.NpcSpawnRules.Seed(1, siteId) % MaximumConditionalHackCount);

    public static bool IsTalocan(int typeId) => typeId is AbandonedTalocanBattleship or DesertedTalocanCruiser or DerelictTalocanFrigate;

    // NPC redemption values in ISK, documented by UniWiki's Sleeper Loot table.
    public static long? BlueLootPriceCents(int typeId) =>
        (SleeperBlueLootType)typeId switch
        {
            SleeperBlueLootType.NeuralNetworkAnalyzer => 200_000L * Eden.Gameplay.Market.MarketDefinitions.CentsPerIsk,
            SleeperBlueLootType.SleeperDataLibrary => 500_000L * Eden.Gameplay.Market.MarketDefinitions.CentsPerIsk,
            SleeperBlueLootType.AncientCoordinatesDatabase => 1_500_000L * Eden.Gameplay.Market.MarketDefinitions.CentsPerIsk,
            SleeperBlueLootType.SleeperDroneAiNexus => 5_000_000L * Eden.Gameplay.Market.MarketDefinitions.CentsPerIsk,
            _ => null,
        };
}
