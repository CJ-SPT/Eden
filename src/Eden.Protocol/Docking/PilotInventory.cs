namespace Eden.Protocol.Docking;

public sealed record InventoryItem(long Id, int TypeId, long OwnerId, long LocationId, int FlagId,
    int Quantity, int GroupId, int CategoryId, double Volume);

public sealed record InventoryChange(InventoryItem Item, InventoryItem? Previous);

public interface IPilotInventory
{
    IReadOnlyList<InventoryItem> List(long pilotId, long locationId);
    IReadOnlyList<InventoryChange> Move(long pilotId, IReadOnlyList<long> itemIds, long sourceId,
        long destinationId, int flagId, int? quantity);
}
