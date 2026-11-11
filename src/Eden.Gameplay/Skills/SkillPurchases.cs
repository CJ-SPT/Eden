using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;

namespace Eden.Gameplay.Skills;

public sealed record SkillPurchaseOffer(
    int TypeId,
    InventoryCategory Category,
    bool Published,
    bool Available,
    double BasePrice,
    double Rank,
    bool Injected
);

public sealed record SkillPurchase(IReadOnlyList<int> TypeIds, long BalanceCents);

public sealed record SkillPurchaseLine(int TypeId, long PriceCents);

public interface IPilotSkillPurchases
{
    SkillPurchase Purchase(long pilotId, IReadOnlyList<int> typeIds);
}

public static class SkillPurchaseRules
{
    // Build 3662168 characterskills.common.purchase.DIRECT_PURCHASE_TAX_RATE.
    public const decimal PriceMultiplier = 1.3m;

    public static long PriceCents(SkillPurchaseOffer offer)
    {
        if (
            offer.Category != InventoryCategory.Skill
            || !offer.Published
            || !offer.Available
            || !double.IsFinite(offer.Rank)
            || offer.Rank <= 0
        )
        {
            throw new InvalidDataException("This skill is unavailable for direct purchase.");
        }
        if (offer.Injected)
        {
            throw new InvalidDataException("The pilot already knows this skill.");
        }
        if (
            !double.IsFinite(offer.BasePrice)
            || offer.BasePrice <= 0
            || offer.BasePrice >= (double)long.MaxValue / MarketDefinitions.CentsPerIsk
        )
        {
            throw new InvalidDataException("This skill has no valid direct purchase price.");
        }
        decimal cents = decimal.Round(
            (decimal)offer.BasePrice * PriceMultiplier * MarketDefinitions.CentsPerIsk,
            0,
            MidpointRounding.AwayFromZero
        );
        if (cents is <= 0 or > long.MaxValue)
        {
            throw new InvalidDataException("This skill has no valid direct purchase price.");
        }
        return (long)cents;
    }
}
