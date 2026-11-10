using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public enum RegionalMarketRole
{
    Mining,
    Industry,
    Hub,
}

public sealed record RegionalStock(int Remaining, int Capacity, long RefreshedTicks);

public sealed record MarketDemandEligibility(
    InventoryCategory Category,
    int GroupId,
    bool Published,
    bool OnMarket,
    double Volume,
    int TechLevel,
    bool Faction,
    bool Resource,
    bool Manufacturable
);

public static class RegionalMarketRules
{
    // Eden economic policy, measured in shared human-active time.
    public static readonly long RecoveryIntervalTicks = TimeSpan.FromMinutes(10).Ticks;
    public const int RecoveryIntervals = 6;
    public const decimal MaximumPriceResponse = 0.15m;
    public const long InitialDemandCents = 150_000_000L * MarketDefinitions.CentsPerIsk;

    // Build 3662168 inventory groups: regular capitals deliberately remain eligible.
    private const int TitanGroup = 30;
    private const int SupercarrierGroup = 659;
    private const int HubHullStock = 5;
    private const int HubChargeStock = 5000;
    private const int HubItemStock = 50;
    private const int SecondaryAssortmentDivisor = 2;
    public const int SecondaryStockDivisor = 5;
    private const decimal SecondaryPriceMargin = 0.05m;

    public static bool CanUnlock(InventoryCategory category, int groupId, bool published, bool onMarket) =>
        published && onMarket && category != InventoryCategory.Blueprint && groupId is not (TitanGroup or SupercarrierGroup);

    public static bool CanDemand(MarketDemandEligibility item) =>
        CanUnlock(item.Category, item.GroupId, item.Published, item.OnMarket)
        && item.Category
            is not (
                InventoryCategory.Skill
                or InventoryCategory.Entity
                or InventoryCategory.Celestial
                or InventoryCategory.Station
                or InventoryCategory.Reaction
                or InventoryCategory.ShipSkin
                or InventoryCategory.ExpertSystem
                or InventoryCategory.Personalization
            )
        && double.IsFinite(item.Volume)
        && item.Volume > 0
        && !item.Faction
        && (item.Resource || item.TechLevel == 1 || item.Manufacturable);

    public static int InitialStock(InventoryCategory category) =>
        category switch
        {
            InventoryCategory.Ship => HubHullStock,
            InventoryCategory.Charge => HubChargeStock,
            _ => HubItemStock,
        };

    public static bool Stocks(int typeId, InventoryCategory category, RegionalMarketRole role) =>
        role == RegionalMarketRole.Hub
        || role == RegionalMarketRole.Industry && category != InventoryCategory.Ship && typeId % SecondaryAssortmentDivisor == 1;

    public static bool Buys(int typeId, InventoryCategory category, bool rawResource, bool material, RegionalMarketRole role) =>
        role == RegionalMarketRole.Hub
        || role == RegionalMarketRole.Mining && rawResource
        || role == RegionalMarketRole.Industry && (material || Stocks(typeId, category, role));

    public static int DemandCapacity(long referenceCents, RegionalMarketRole role, long elapsedTicks)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(referenceCents);
        long budget = InitialDemandCents * Capacity(1, elapsedTicks);
        if (role != RegionalMarketRole.Hub)
        {
            budget /= SecondaryStockDivisor;
        }
        return (int)Math.Clamp(budget / referenceCents, 1, int.MaxValue);
    }

    public static long SnapshotAnchor(long cents, RegionalMarketRole role, bool bid) =>
        Math.Max(
            1,
            PersonalMarketRules.Round(
                cents
                    * (
                        role == RegionalMarketRole.Hub ? 1m
                        : bid ? 1m - SecondaryPriceMargin
                        : 1m + SecondaryPriceMargin
                    )
            )
        );

    public static long NextCapacityChange(long elapsedTicks)
    {
        const int growthHours = 8;
        const int maximumGrowthStages = 3;
        long interval = TimeSpan.FromHours(growthHours).Ticks;
        long stage = elapsedTicks / interval;
        return stage >= maximumGrowthStages ? long.MaxValue : (stage + 1) * interval;
    }

    public static int Capacity(int initial, long elapsedTicks)
    {
        const int growthHours = 8;
        const int maximumGrowthStages = 3;
        int stage = (int)Math.Min(maximumGrowthStages, elapsedTicks / TimeSpan.FromHours(growthHours).Ticks);
        return checked(initial * (1 + stage));
    }

    public static RegionalStock Recover(RegionalStock stock, int capacity, long elapsedTicks, bool demand = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(elapsedTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        long intervals = Math.Max(0, elapsedTicks - stock.RefreshedTicks) / RecoveryIntervalTicks;
        long increment = demand ? ((long)capacity + RecoveryIntervals - 1) / RecoveryIntervals : Math.Max(1, capacity / RecoveryIntervals);
        long replenished = Math.Min(capacity, Math.Min(intervals, capacity) * increment);
        return new(
            (int)Math.Min(capacity, (long)stock.Remaining + replenished),
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
