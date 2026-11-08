using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public sealed record LoyaltyRequiredItem(int TypeId, int Quantity);

public sealed record LoyaltyOffer(
    int Id,
    int CorporationId,
    int TypeId,
    int Quantity,
    long LpCost,
    long IskCents,
    IReadOnlyList<LoyaltyRequiredItem> RequiredItems
);

public sealed record LoyaltyPurchaseCost(long Points, long IskCents, int Quantity, IReadOnlyList<LoyaltyRequiredItem> RequiredItems);

public sealed record LoyaltyPurchase(
    long PointsBefore,
    long PointsAfter,
    long BalanceCents,
    IReadOnlyList<InventoryChange> Changes,
    int? CorporationId = null,
    int? WalletKey = null
);

public sealed record LoyaltyExchange(long SourceBefore, long SourceAfter, long DestinationBefore, long DestinationAfter);

public interface IPilotLoyaltyStore
{
    void CheckStation(long pilotId, int stationId);
    IReadOnlyList<LoyaltyOffer> Offers(long pilotId, int corporationId);
    LoyaltyPurchase Purchase(long pilotId, int corporationId, int offerId, int count, bool corporate = false);
    LoyaltyExchange ExchangeConcord(long pilotId, int corporationId, long points);
}

public static class LoyaltyStoreRules
{
    // Bound a native multi-buy to protect inventory and notification work.
    public const int MaximumOffersPerPurchase = 1000;

    // Build 3662168 appConst.ownerCONCORD.
    public const int ConcordCorporation = 1000125;

    public static long ExchangeCost(long destinationPoints, decimal rate)
    {
        if (destinationPoints <= 0 || rate is <= 0 or > 1)
        {
            throw new InvalidDataException("Choose a positive LP amount and an available exchange rate.");
        }
        try
        {
            return checked((long)decimal.Ceiling(destinationPoints / rate));
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("The LP exchange is too large.", error);
        }
    }

    public static LoyaltyPurchaseCost Cost(LoyaltyOffer offer, int count)
    {
        if (
            count is < 1 or > MaximumOffersPerPurchase
            || offer.Id <= 0
            || offer.CorporationId <= 0
            || offer.TypeId <= 0
            || offer.Quantity <= 0
            || offer.LpCost < 0
            || offer.IskCents < 0
            || offer.RequiredItems.Any(item => item.TypeId <= 0 || item.Quantity <= 0)
            || offer.RequiredItems.Select(item => item.TypeId).Distinct().Count() != offer.RequiredItems.Count
        )
        {
            throw new InvalidDataException("Invalid loyalty store offer or purchase quantity.");
        }
        try
        {
            return new(
                checked(offer.LpCost * count),
                checked(offer.IskCents * count),
                checked(offer.Quantity * count),
                offer.RequiredItems.Select(item => item with { Quantity = checked(item.Quantity * count) }).ToArray()
            );
        }
        catch (OverflowException error)
        {
            throw new InvalidDataException("The loyalty store purchase is too large.", error);
        }
    }

    public static void CheckFunds(long points, long cents, LoyaltyPurchaseCost cost)
    {
        if (points < cost.Points || cents < cost.IskCents || points < 0 || cents < 0)
        {
            throw new InvalidDataException("You do not have enough loyalty points or ISK for this purchase.");
        }
    }
}
