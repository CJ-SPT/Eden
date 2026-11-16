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

public static class SleeperRules
{
    // Build 3662168 labels the native Sleeper dungeon faction "Unknown".
    public const int FactionId = 500021;
    public const int SystemOwner = 1;

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
