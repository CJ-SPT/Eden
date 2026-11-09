namespace Eden.Gameplay.Inventory;

public sealed record InventoryStackSplit(int Remainder, int Quantity);

public sealed record InventoryStackMergeQuantities(int Source, int Target);

public static class InventoryStackRules
{
    public const int MaximumMergeOperations = 1000;

    public static void CheckMergeSelection(IReadOnlyList<InventoryStackMerge> operations)
    {
        var sources = operations.Select(operation => operation.SourceItemId).ToHashSet();
        if (
            operations.Count is 0 or > MaximumMergeOperations
            || sources.Count != operations.Count
            || operations.Any(operation =>
                operation.SourceItemId <= 0 || operation.TargetItemId <= 0 || sources.Contains(operation.TargetItemId)
            )
        )
        {
            throw new InvalidDataException("Choose distinct source stacks and separate destination stacks.");
        }
    }

    public static InventoryStackMergeQuantities Merge(InventoryItem source, InventoryItem target, int quantity)
    {
        if (
            source.Id == target.Id
            || source.OwnerId != target.OwnerId
            || source.TypeId != target.TypeId
            || source.Volume != target.Volume
            || source.CrystalDamage != target.CrystalDamage
            || source.Singleton
            || target.Singleton
            || source.Online
            || target.Online
            || quantity <= 0
            || quantity > source.Quantity
            || target.Quantity <= 0
        )
        {
            throw new InvalidDataException("Merge compatible packaged stacks and an available positive quantity.");
        }
        return new(source.Quantity - quantity, CombinedQuantity([target.Quantity, quantity]));
    }

    public static int CombinedQuantity(IEnumerable<int> quantities) =>
        quantities.Aggregate(0, (total, quantity) => checked(total + quantity));

    public static InventoryStackSplit Split(InventoryItem item, int quantity)
    {
        if (item.Singleton || item.Online || quantity <= 0 || quantity >= item.Quantity)
        {
            throw new InvalidDataException("Split a packaged stack into two positive quantities.");
        }
        return new(item.Quantity - quantity, quantity);
    }
}
