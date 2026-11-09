using Eden.Gameplay.Redemption;

namespace Eden.Gameplay.Inventory;

public enum ItemSpawnDestination
{
    StationHangar,
    ShipCargo,
}

public sealed record ItemSpawnRequest(int TypeId, int Quantity, ItemSpawnDestination Destination);

public interface IPilotItemSpawning
{
    InventoryChange Create(long pilotId, long expectedShipId, ItemSpawnRequest request);
}

public static class ItemSpawnRules
{
    public static void Check(ItemSpawnRequest request, InventoryCategory category, bool docked)
    {
        if (request.TypeId <= 0 || request.Quantity <= 0 || !Enum.IsDefined(request.Destination))
        {
            throw new InvalidDataException("Choose a known item type, a positive quantity and a supported destination.");
        }
        // Blueprints are original physical items. Licenses, currencies, NPCs and
        // map objects require separate authoritative state and cannot be item stacks.
        if (!RedemptionRules.Deliverable(category) && category != InventoryCategory.Blueprint)
        {
            throw new InvalidDataException("This type cannot be created as a physical inventory item.");
        }
        if (request.Destination == ItemSpawnDestination.StationHangar && !docked)
        {
            throw new InvalidDataException("Dock before creating items in your station hangar, or use /load me for cargo.");
        }
    }

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
