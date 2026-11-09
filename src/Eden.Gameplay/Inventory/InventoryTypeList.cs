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
