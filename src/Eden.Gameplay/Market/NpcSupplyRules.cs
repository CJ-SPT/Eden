using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Market;

public enum NpcMarketKind
{
    Development,
    Institutional,
}

public sealed record NpcSupplyType(int TypeId, InventoryCategory Category, bool Published, decimal BasePriceIsk, bool Offered);

public static class NpcSupplyRules
{
    // The client exports corporation/type eligibility and base prices, but not
    // live server quantities or dynamic NPC price adjustments. Use an explicit,
    // replenishing institutional stock policy; never seed finished products here.
    public const int Stock = 1000;
    public const int MaximumStock = 1_000_000;

    public static void ValidateStock(int stock)
    {
        if (stock is < 1 or > MaximumStock)
        {
            throw new InvalidDataException("NPC institutional stock must be between one and one million units.");
        }
    }

    public static long? Price(NpcSupplyType type)
    {
        if (
            type.TypeId <= 0
            || !type.Offered
            || !type.Published
            || type.Category is not (InventoryCategory.Blueprint or InventoryCategory.Skill)
            || type.BasePriceIsk <= 0
            || type.BasePriceIsk > MarketDefinitions.MaximumOrderPrice
        )
        {
            return null;
        }
        long cents = PersonalMarketRules.Round(type.BasePriceIsk * MarketDefinitions.CentsPerIsk);
        return cents > 0 ? cents : null;
    }
}
