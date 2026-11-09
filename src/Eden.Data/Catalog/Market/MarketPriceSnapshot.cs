namespace Eden.Data.Catalog.Market;

public sealed record MarketPriceSnapshot(
    string Source,
    int StationId,
    DateTimeOffset StartedAt,
    DateTimeOffset RetrievedAt,
    string Statistic,
    MarketPriceQuote[] Quotes
);

public sealed record MarketPriceQuote(int TypeId, long? BuyCents, long? SellCents, int LocationId);
