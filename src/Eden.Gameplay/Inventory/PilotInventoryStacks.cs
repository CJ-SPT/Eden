namespace Eden.Gameplay.Inventory;

public interface IPilotInventoryStacks
{
    IReadOnlyList<InventoryChange> StackAll(long pilotId, long locationId, int flagId);
}
