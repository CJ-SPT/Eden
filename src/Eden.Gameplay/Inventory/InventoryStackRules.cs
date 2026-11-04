namespace Eden.Gameplay.Inventory;

public sealed record InventoryStackSplit(int Remainder, int Quantity);

public static class InventoryStackRules
{
    public static InventoryStackSplit Split(InventoryItem item, int quantity)
    {
        if (item.Singleton || item.Online || quantity <= 0 || quantity >= item.Quantity)
        {
            throw new InvalidDataException("Split a packaged stack into two positive quantities.");
        }
        return new(item.Quantity - quantity, quantity);
    }
}
