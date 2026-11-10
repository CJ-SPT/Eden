namespace Eden.Gameplay.Market;

public sealed record MarketValuation(int TypeId, decimal? AveragePriceIsk, decimal? AdjustedPriceIsk);

public interface IMarketValuations
{
    IReadOnlyList<MarketValuation> Prices();
}

public static class MarketValuationRules
{
    public static decimal? EstimateIsk(long? buyCents, long? sellCents)
    {
        decimal? cents = buyCents.HasValue && sellCents.HasValue ? ((decimal)buyCents.Value + sellCents.Value) / 2 : buyCents ?? sellCents;
        return cents is > 0 ? cents / MarketDefinitions.CentsPerIsk : null;
    }
}
