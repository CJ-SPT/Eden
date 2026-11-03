namespace Eden.Gameplay.Market;

public sealed record MarketSeedType(int TypeId, bool Published, int? MarketGroupId);

public static class TestingMarketRules
{
    public const int SeedVersion = 1;
    public const long PriceCents = MarketDefinitions.CentsPerIsk;
    public const int Stock = 1_000_000;
    public const long GrantCents = 1_000_000L * MarketDefinitions.CentsPerIsk;
    public const long FirstGrantJournalId = 8_000_000_000_000;

    public static bool IsEligible(MarketSeedType type) => type.TypeId > 0 && type.Published && type.MarketGroupId > 0;

    public static long GrantBalance(long balanceCents) => checked(balanceCents + GrantCents);
}
