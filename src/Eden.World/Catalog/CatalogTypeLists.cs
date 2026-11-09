using System.Collections.Frozen;
using Eden.Data.Catalog;
using Eden.Gameplay.Inventory;

namespace Eden.World.Catalog;

internal static class CatalogTypeLists
{
    internal static InventoryTypeList Read(CatalogDbContext catalog, long id)
    {
        var list = catalog.Require(CatalogData.Client.Typelist, id);
        return new(
            (list.IncludedCategoryIDs ?? []).ToFrozenSet(),
            (list.IncludedGroupIDs ?? []).ToFrozenSet(),
            (list.IncludedTypeIDs ?? []).ToFrozenSet(),
            (list.ExcludedCategoryIDs ?? []).ToFrozenSet(),
            (list.ExcludedGroupIDs ?? []).ToFrozenSet(),
            (list.ExcludedTypeIDs ?? []).ToFrozenSet()
        );
    }
}
