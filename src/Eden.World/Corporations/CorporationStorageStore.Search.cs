using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.World.Catalog;
using Eden.World.Runtime;

namespace Eden.World.Corporations;

internal sealed partial class CorporationStorageStore
{
    public IReadOnlyList<int> SearchOfficeAssets(long actorId, CorporationOfficeAssetSearch search)
    {
        if (search.CategoryId < 0 || search.GroupId < 0 || search.TypeId < 0 || search.MinimumQuantity < 0)
        {
            throw new InvalidDataException("Invalid corporation office asset filter.");
        }
        var live =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation office asset search requires the resident world.");
        return live.Economy.Inspect<IReadOnlyList<int>>(world =>
        {
            var corporation = CorporationRoleStore.Current(world, actorId);
            var access = CorporationRoleStore.Effective(world, actorId, corporation);
            if (!CorporationStorageRules.CanReadOfficeAssetReport(access))
            {
                throw new InvalidDataException("Only corporation directors and accountants can search office assets.");
            }
            var offices = world
                .Filter<SavedCorporationOffice>(row => row.CorporationId == corporation.Id)
                .OrderBy(row => row.SystemId)
                .ThenBy(row => row.StationId)
                .ToArray();
            if (!search.HasFilters || offices.Length == 0)
            {
                return offices.Select(row => row.StationId).ToArray();
            }

            using CatalogDbContext catalog = new(database.CatalogPath);
            var officeIds = offices.Select(row => row.Id).ToHashSet();
            HashSet<long> matched = [];
            foreach (var item in world.Filter<OwnedItem>(row => row.OwnerId == corporation.Id).AsEnumerable())
            {
                if (
                    officeIds.Contains(item.LocationId)
                    && CorporationStorageRules.CanReadOfficeAssetReportFlag(access, item.FlagId)
                    && Matches(item.TypeId, item.Quantity)
                )
                {
                    matched.Add(item.LocationId);
                }
            }
            if (search.MinimumQuantity <= 1)
            {
                foreach (var ship in world.Filter<Ship>(row => row.OwnerId == corporation.Id).AsEnumerable())
                {
                    if (
                        ship.StoredInId is { } officeId
                        && officeIds.Contains(officeId)
                        && CorporationStorageRules.CanReadOfficeAssetReportFlag(access, ship.StorageFlagId)
                        && Matches(ship.TypeId, 1)
                    )
                    {
                        matched.Add(officeId);
                    }
                }
            }
            return offices.Where(row => matched.Contains(row.Id)).Select(row => row.StationId).ToArray();

            bool Matches(int typeId, int quantity)
            {
                if (quantity < search.MinimumQuantity || search.TypeId != 0 && search.TypeId != typeId)
                {
                    return false;
                }
                if (search.GroupId == 0 && search.CategoryId == 0)
                {
                    return true;
                }
                return catalog.FittingType(typeId) is { } type
                    && (search.GroupId == 0 || type.GroupId == search.GroupId)
                    && (search.CategoryId == 0 || type.CategoryId == search.CategoryId);
            }
        });
    }
}
