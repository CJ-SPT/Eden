namespace Eden.Gameplay.Inventory;

public sealed record InventoryTypeList(
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
    public bool Contains(InventoryItem item) => Contains(item.TypeId, item.GroupId, item.CategoryId);

    public bool Contains(int typeId, int groupId, int categoryId) =>
        !ExcludedTypes.Contains(typeId)
        && (
            IncludedTypes.Contains(typeId)
            || (
                !ExcludedGroups.Contains(groupId)
                && (
                    IncludedGroups.Contains(groupId)
                    || (IncludedCategories.Contains(categoryId) && !ExcludedCategories.Contains(categoryId))
                )
            )
        );
}
