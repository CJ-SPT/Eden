using Eden.Gameplay.Market;

namespace Eden.Gameplay.Capsuleers;

public static class CapsuleerRegionalStockRules
{
    public const int MaximumJumps = 3;
    public const int BudgetDivisor = 2;
    public const int MaximumLossesPerPilot = 2;
    public const int MaximumLossesPerHull = 2;
    public static readonly TimeSpan FleetLossMemory = TimeSpan.FromDays(1);
    public static readonly TimeSpan ReviewInterval = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan BidWindow = TimeSpan.FromHours(6);
}

public sealed record CapsuleerFleetLoss(long ShipId, int CorporationId, long Time);

public sealed record RegionalStockTrip(int Headquarters, int Supplier, MarketPurchase[] Purchases)
{
    public int? DeliveryStation { get; init; }
    public long[]? OrderIds { get; init; }
    public long BidDeadline { get; init; }
}
