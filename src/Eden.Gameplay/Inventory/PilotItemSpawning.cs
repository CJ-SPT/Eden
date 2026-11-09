using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;

namespace Eden.Gameplay.Inventory;

public enum ItemSpawnDestination
{
    StationHangar,
    ShipCargo,
}

public sealed record ItemSpawnRequest(int TypeId, int Quantity, ItemSpawnDestination Destination);

public abstract record ItemSpawnResult
{
    public sealed record Inventory(InventoryChange Change) : ItemSpawnResult;

    public sealed record Redemption(RedeemToken Token) : ItemSpawnResult;
}

public interface IPilotItemSpawning
{
    ItemSpawnResult Create(long pilotId, long expectedShipId, ItemSpawnRequest request);
}

public static class ItemSpawnRules
{
    public static void Check(ItemSpawnRequest request, InventoryCategory category, bool docked, int groupId = 0)
    {
        if (request.TypeId <= 0 || request.Quantity <= 0 || !Enum.IsDefined(request.Destination))
        {
            throw new InvalidDataException("Choose a known item type, a positive quantity and a supported destination.");
        }
        // Developer creation admits physical consumables even when their activation
        // mechanic is unavailable. Map objects and NPCs still need world spawning.
        bool container = category == InventoryCategory.Celestial && Enum.IsDefined((PersonalAssetContainerGroup)groupId);
        if (!Physical(category) && !container)
        {
            throw new InvalidDataException("This type cannot be created as a physical inventory item.");
        }
        if (request.Destination == ItemSpawnDestination.StationHangar && !docked && !AutomaticReward(request.TypeId))
        {
            throw new InvalidDataException("Dock before creating items in your station hangar, or use /load me for cargo.");
        }
    }

    public static bool AutomaticReward(int typeId) => SkillPointRewards.Amount(typeId) > 0 || CurrencyRewardRules.Kind(typeId).HasValue;

    private static bool Physical(InventoryCategory category) =>
        RedemptionRules.Deliverable(category)
        || category
            is InventoryCategory.Accessories
                or InventoryCategory.Blueprint
                or InventoryCategory.Starbase
                or InventoryCategory.Reaction
                or InventoryCategory.Decryptor
                or InventoryCategory.PlanetaryIndustry
                or InventoryCategory.SpecialEditionAssets
                or InventoryCategory.ShipSkin
                or InventoryCategory.ExpertSystem
                or InventoryCategory.Personalization
                or InventoryCategory.ColonyResource;

    public static void CheckCapacity(double volume, int quantity, double used, double capacity)
    {
        double required = volume * quantity;
        if (!double.IsFinite(required) || volume < 0 || quantity <= 0 || !double.IsFinite(used) || !double.IsFinite(capacity))
        {
            throw new InvalidDataException("Invalid item volume or cargo capacity.");
        }
        double available = Math.Max(0, capacity - used);
        if (required > available + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(available, required);
        }
    }
}
