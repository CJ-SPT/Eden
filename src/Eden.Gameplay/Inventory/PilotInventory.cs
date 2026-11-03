using Eden.Gameplay.Dogma;

namespace Eden.Gameplay.Inventory;

public sealed record InventoryItem(
    long Id,
    int TypeId,
    long OwnerId,
    long LocationId,
    int FlagId,
    int Quantity,
    int GroupId,
    int CategoryId,
    double Volume,
    bool Singleton = false,
    bool Online = false
);

public sealed record InventoryChange(InventoryItem Item, InventoryItem? Previous);

public sealed class CargoCapacityException(double available, double required) : Exception("The ship does not have enough cargo space.")
{
    public double Available { get; } = available;
    public double Required { get; } = required;
}

public interface IPilotInventory
{
    IReadOnlyList<InventoryItem> List(long pilotId, long locationId);
    IReadOnlyList<InventoryChange> Move(
        long pilotId,
        IReadOnlyList<long> itemIds,
        long sourceId,
        long destinationId,
        int flagId,
        int? quantity
    );
}

public interface IPilotFitting
{
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes(int typeId);
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId);
    IReadOnlyList<InventoryChange> Fit(long pilotId, IReadOnlyList<long> itemIds, long sourceId, int flagId);
    InventoryChange SetOnline(long pilotId, long shipId, long itemId, bool online);
}
