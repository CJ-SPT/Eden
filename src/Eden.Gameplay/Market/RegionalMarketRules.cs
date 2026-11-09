using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public enum RegionalMarketRole
{
    Mining,
    Industry,
    Hub,
}

public sealed record RegionalStock(int Remaining, int Capacity, long RefreshedTicks);

public static class RegionalMarketRules
{
    // Eden economic policy, measured in shared human-active time.
    public static readonly long RecoveryIntervalTicks = TimeSpan.FromMinutes(10).Ticks;
    public const int RecoveryIntervals = 6;
    public const int IndustryHours = 4;
    public const int FirstHullHours = 8;
    public const int CombatHullHours = 16;
    public const int HaulerHours = 24;
    public const decimal MaximumPriceResponse = 0.15m;

    public static int Capacity(int initial, long elapsedTicks)
    {
        const int growthHours = 8;
        const int maximumGrowthStages = 3;
        int stage = (int)Math.Min(maximumGrowthStages, elapsedTicks / TimeSpan.FromHours(growthHours).Ticks);
        return checked(initial * (1 + stage));
    }

    public static long Anchor(decimal valueCents, RegionalMarketRole role, InventoryCategory category, bool bid)
    {
        decimal multiplier = category switch
        {
            InventoryCategory.Asteroid => role == RegionalMarketRole.Mining ? 0.75m : 1.10m,
            InventoryCategory.Material => bid
                ? role == RegionalMarketRole.Mining
                    ? 0.60m
                    : 1.25m
                : role == RegionalMarketRole.Mining
                    ? 1.00m
                    : 1.65m,
            _ => bid ? 0.75m : 1.50m,
        };
        // Retain a positive spread even for references below one ISK cent.
        return Math.Max(bid ? 1 : 2, PersonalMarketRules.Round(valueCents * multiplier));
    }

    public static RegionalStock Recover(RegionalStock stock, int capacity, long elapsedTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        long intervals = Math.Max(0, elapsedTicks - stock.RefreshedTicks) / RecoveryIntervalTicks;
        long replenished = Math.Min(capacity, intervals * Math.Max(1, capacity / RecoveryIntervals));
        return new(
            (int)Math.Min(capacity, stock.Remaining + replenished),
            capacity,
            checked(stock.RefreshedTicks + intervals * RecoveryIntervalTicks)
        );
    }

    public static long Quote(long anchorCents, bool bid, int remaining, int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorCents);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        decimal depletion = 1 - Math.Clamp((decimal)remaining / capacity, 0, 1);
        decimal multiplier = 1 + (bid ? -1 : 1) * MaximumPriceResponse * depletion;
        return Math.Max(1, PersonalMarketRules.Round(anchorCents * multiplier));
    }
}
