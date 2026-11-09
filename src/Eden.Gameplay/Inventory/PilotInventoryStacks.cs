namespace Eden.Gameplay.Inventory;

public sealed record InventoryStackMerge(long SourceItemId, long TargetItemId, int Quantity);

public interface IPilotInventoryStacks
{
    IReadOnlyList<InventoryChange> StackAll(long pilotId, long locationId, int flagId);
    IReadOnlyList<InventoryChange> Merge(
        long pilotId,
        long sourceLocationId,
        long destinationLocationId,
        IReadOnlyList<InventoryStackMerge> operations
    );
}
