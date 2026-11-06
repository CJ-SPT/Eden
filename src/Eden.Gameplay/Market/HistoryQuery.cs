namespace Eden.Gameplay.Market;

// File-time bounds are inclusive at From and exclusive at Until. A missing end is unbounded.
public sealed record HistoryPeriod(long From, long? Until = null);

public sealed record MarketTransactionQuery(long? Before = null, int Limit = 1000);
