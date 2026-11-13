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
    // from an explicitly captured market snapshot; quantity is Eden policy.
    public const int Stock = 1000;
    public const int MaximumStock = 1_000_000;

    public static void ValidateStock(int stock)
    {
        if (stock is < 1 or > MaximumStock)
        {
            throw new InvalidDataException("NPC institutional stock must be between one and one million units.");
        }
    }

    // These catalog-backed originals and skill are required to start the refinery and deep-core chains.
    public static bool UsesIndustrialCatalogPrice(int typeId) =>
        typeId
            is 11395 // Deep Core Mining
                or 12109 // Deep Core Mining Laser I Blueprint
                or 36977 // Athanor Blueprint
                or 36978 // Tatara Blueprint
                or 37032 // Standup Reprocessing Facility I Blueprint
                or 37034 // Standup Moon Drill I Blueprint
                or 45540 // Standup Composite Reactor I Blueprint
                or 45541 // Standup Hybrid Reactor I Blueprint
                or 45542; // Standup Biochemical Reactor I Blueprint

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
