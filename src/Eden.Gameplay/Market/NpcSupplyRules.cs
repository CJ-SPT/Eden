using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public enum NpcMarketKind
{
    Development,
    Institutional,
    Regional,
}

public sealed record NpcSupplyType(int TypeId, InventoryCategory Category, bool Published, decimal ReferencePriceIsk, bool Offered);

public static class NpcSupplyRules
{
    // Corporation/type eligibility comes from the pinned catalog. Prices come
    // from a captured market snapshot, falling back to catalog base prices for
    // missing sell quotes; quantity is Eden policy.
    public const int Stock = 1000;
    public const int MaximumStock = 1_000_000;

    public static void ValidateStock(int stock)
    {
        if (stock is < 1 or > MaximumStock)
        {
            throw new InvalidDataException("NPC institutional stock must be between one and one million units.");
        }
    }

    // Build 3662168 tags blueprint 37346 as Tech II although its manufactured
    // rig is Tech I. Manufacturing outputs determine the tier when available.
    public static bool CanSupplyBlueprint(int blueprintTechLevel, IReadOnlyList<int> productTechLevels) =>
        productTechLevels.Count > 0 && productTechLevels.All(level => level == 1)
        || blueprintTechLevel <= 1 && productTechLevels.All(level => level <= 1);

    public static long? Price(NpcSupplyType type)
    {
        if (
            type.TypeId <= 0
            || !type.Offered
            || !type.Published
            || type.Category is InventoryCategory.Station or InventoryCategory.ShipSkin or InventoryCategory.Personalization
            || type.ReferencePriceIsk <= 0
            || type.ReferencePriceIsk > MarketDefinitions.MaximumOrderPrice
        )
        {
            return null;
        }
        long cents = PersonalMarketRules.Round(type.ReferencePriceIsk * MarketDefinitions.CentsPerIsk);
        return cents > 0 ? cents : null;
    }
}
