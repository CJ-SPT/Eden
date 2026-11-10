namespace Eden.Gameplay.Market;

public sealed record MarketReferenceQuote(int TypeId, long? BuyCents, long? SellCents);

public static class MarketReferenceHistoryRules
{
    public const int Days = 30;
    public const int BasisPoints = 10000;
    public const int MaximumVariationBasisPoints = 300;
    private const int IntradaySpreadBasisPoints = 50;
    private const int MinimumVolumePercent = 75;
    private const int VolumeVariationPercent = 50;
    private const int MaximumDailyOrders = 20;

    // Fixed integer mixing keeps reference output stable across processes and runtime versions.
    private const uint TypeMix = 0x9E3779B9;
    private const uint DayMix = 0x85EBCA6B;
    private const int MixShift = 16;

    public static IReadOnlyList<MarketHistoryDay> Generate(MarketReferenceQuote quote, DateOnly lastDate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quote.TypeId);
        if (MarketValuationRules.EstimateIsk(quote.BuyCents, quote.SellCents) is not { } priceIsk)
        {
            return [];
        }
        decimal basis = priceIsk * MarketDefinitions.CentsPerIsk;
        int capacity = RegionalMarketRules.DemandCapacity((long)decimal.Ceiling(basis), RegionalMarketRole.Hub, 0);
        MarketHistoryDay[] days = new MarketHistoryDay[Days];
        for (int index = 0; index < Days; index++)
        {
            uint sample = unchecked((uint)quote.TypeId * TypeMix + (uint)index * DayMix);
            sample ^= sample >> MixShift;
            int variation = index == Days - 1 ? 0 : (int)(sample % (MaximumVariationBasisPoints * 2 + 1)) - MaximumVariationBasisPoints;
            decimal average = Math.Max(1, basis * (BasisPoints + variation) / BasisPoints);
            long low = Math.Max(1, (long)decimal.Floor(average * (BasisPoints - IntradaySpreadBasisPoints) / BasisPoints));
            long high = Math.Max(low, (long)decimal.Ceiling(average * (BasisPoints + IntradaySpreadBasisPoints) / BasisPoints));
            int volumePercent = MinimumVolumePercent + (int)((sample >> MixShift) % (VolumeVariationPercent + 1));
            long volume = Math.Max(1, (long)capacity * volumePercent / 100);
            int orders = (int)Math.Min(volume, 1 + sample % MaximumDailyOrders);
            long date = lastDate.AddDays(index - Days + 1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).ToFileTimeUtc();
            days[index] = new(quote.TypeId, date, low, high, average, volume, orders) { Source = MarketHistorySource.SimulatedReference };
        }
        return days;
    }
}
