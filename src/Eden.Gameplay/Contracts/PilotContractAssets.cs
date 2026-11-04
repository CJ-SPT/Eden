using Eden.Gameplay.Inventory;

namespace Eden.Gameplay.Contracts;

public sealed record ContractContainerCount(long ItemId, int Count);

public sealed record ContractStackSplit(int Remainder, int Quantity);

public interface IPilotContractAssets
{
    IReadOnlyList<InventoryItem> Station(long pilotId, int stationId);
    IReadOnlyList<InventoryItem> Contents(long pilotId, int stationId, long itemId);
    IReadOnlyList<ContractContainerCount> Counts(long pilotId, int stationId, IReadOnlyList<long> itemIds);
    IReadOnlyList<InventoryChange> Split(long pilotId, int stationId, long itemId, int quantity);
}

public static class ContractAssetRules
{
    // Build 3662168 contractscommon.MAX_NUM_ITEMS bounds a creation selection.
    public const int MaximumSelection = 500;

    public static void CheckSelection(IReadOnlyList<long> itemIds)
    {
        if (itemIds.Count > MaximumSelection || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new InvalidDataException("Choose distinct owned station items for the contract.");
        }
    }

    public static ContractStackSplit Split(InventoryItem item, int quantity)
    {
        if (item.Singleton || item.Online || quantity <= 0 || quantity >= item.Quantity)
        {
            throw new InvalidDataException("Split a packaged stack into two positive quantities.");
        }
        return new(item.Quantity - quantity, quantity);
    }
}
