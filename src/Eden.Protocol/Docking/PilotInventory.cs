namespace Eden.Protocol.Docking;

public sealed record InventoryItem(long Id, int TypeId, long OwnerId, long LocationId, int FlagId,
    int Quantity, int GroupId, int CategoryId, double Volume);

public sealed record InventoryChange(InventoryItem Item, InventoryItem? Previous);

public sealed class CargoCapacityException(double available, double required) : Exception("The ship does not have enough cargo space.")
{
    public double Available { get; } = available;
    public double Required { get; } = required;
}

public interface IPilotInventory
{
    IReadOnlyList<InventoryItem> List(long pilotId, long locationId);
    IReadOnlyList<InventoryChange> Move(long pilotId, IReadOnlyList<long> itemIds, long sourceId,
        long destinationId, int flagId, int? quantity);
}
