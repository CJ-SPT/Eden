namespace Eden.Gameplay.Inventory;

public interface IPilotItemTrash
{
    IReadOnlyList<InventoryChange> Trash(long pilotId, int stationId, IReadOnlyList<long> itemIds);
}

public sealed record ItemTrashState(InventoryItem Item, long PilotId, bool ActiveShip, bool Committed, bool Restricted, bool Selected);

public sealed record ItemTrashTypeList(
    IReadOnlySet<long> IncludedCategories,
    IReadOnlySet<long> IncludedGroups,
    IReadOnlySet<long> IncludedTypes,
    IReadOnlySet<long> ExcludedCategories,
    IReadOnlySet<long> ExcludedGroups,
    IReadOnlySet<long> ExcludedTypes
)
{
    // Native GetTypeIDsFromTypeList applies exclusions at each level before
    // unioning the next, more specific inclusion set.
    public bool Contains(InventoryItem item) =>
        !ExcludedTypes.Contains(item.TypeId)
        && (
            IncludedTypes.Contains(item.TypeId)
            || (
                !ExcludedGroups.Contains(item.GroupId)
                && (
                    IncludedGroups.Contains(item.GroupId)
                    || (IncludedCategories.Contains(item.CategoryId) && !ExcludedCategories.Contains(item.CategoryId))
                )
            )
        );
}

public static class ItemTrashRules
{
    public const int MaximumSelection = 1000;

    public static void CheckSelection(IReadOnlyList<long> itemIds)
    {
        if (itemIds.Count is 0 or > MaximumSelection || itemIds.Any(id => id <= 0) || itemIds.Distinct().Count() != itemIds.Count)
        {
            throw new InvalidDataException("Select distinct items to trash.");
        }
    }

    public static void Check(ItemTrashState state)
    {
        if (
            state.Item.OwnerId != state.PilotId
            || state.ActiveShip
            || state.Committed
            || state.Restricted
            || (state.Selected && (state.Item.Online || InventoryDefinitions.IsFittedSlot(state.Item.FlagId)))
        )
        {
            throw new InvalidDataException(
                "This item or its contents cannot be trashed. Use inactive personal assets outside contracts and jobs."
            );
        }
    }
}
