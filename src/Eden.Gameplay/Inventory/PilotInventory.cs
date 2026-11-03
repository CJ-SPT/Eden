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
    IReadOnlyList<InventoryChange> Move(long pilotId, long destinationId, InventoryMoveRequest request);
}

public interface IPilotFitting
{
    IReadOnlyDictionary<DogmaAttributeId, double> Attributes(int typeId);
    IReadOnlyDictionary<DogmaAttributeId, double> ShipAttributes(long pilotId);
    double SignatureRadius(long itemId, int typeId);
    IReadOnlyList<InventoryChange> Fit(long pilotId, IReadOnlyList<long> itemIds, long sourceId, int flagId);
    IReadOnlyList<FittingSkillBonus> SkillBonuses(long pilotId);
    IReadOnlyDictionary<DogmaAttributeId, double> ModuleAttributes(long pilotId, InventoryItem module);
    IReadOnlyList<InventoryChange> SetOnline(long pilotId, long shipId, long itemId, bool online);
}

public static class PilotInventoryMoves
{
    public static IReadOnlyList<InventoryChange> Move(
        this IPilotInventory inventory,
        long pilotId,
        IReadOnlyList<long> items,
        long sourceId,
        long destinationId,
        int flag,
        int? quantity
    ) => inventory.Move(pilotId, destinationId, new(items, sourceId, flag, quantity));
}
