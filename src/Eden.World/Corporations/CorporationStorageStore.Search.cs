using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Corporations;

internal sealed partial class CorporationStorageStore
{
    public IReadOnlyList<int> SearchOfficeAssets(long actorId, CorporationOfficeAssetSearch search)
    {
        CheckAssetSearch(search);
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
                    && MatchesAssetSearch(catalog, search, item.TypeId, item.Quantity)
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
                        && MatchesAssetSearch(catalog, search, ship.TypeId, 1)
                    )
                    {
                        matched.Add(officeId);
                    }
                }
            }
            return offices.Where(row => matched.Contains(row.Id)).Select(row => row.StationId).ToArray();
        });
    }

    public IReadOnlyList<CorporationDeliveryAssetLocation> DeliveryAssetLocations(
        long actorId,
        int corporationId,
        CorporationOfficeAssetSearch? search = null
    )
    {
        if (search is not null)
        {
            CheckAssetSearch(search);
        }
        var live =
            LiveWorld.Find(database.Path)
            ?? throw new InvalidDataException("Corporation delivery asset reports require the resident world.");
        return live.Economy.Inspect<IReadOnlyList<CorporationDeliveryAssetLocation>>(world =>
        {
            if (!CanReadDeliveryAssetReport(world, actorId, corporationId))
            {
                // The pinned assets panel requests locations before displaying its own role hint.
                if (search is null)
                {
                    return [];
                }
                throw new InvalidDataException("Only corporation directors and accountants can search Market Deliveries.");
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            return world
                .Filter<OwnedItem>(row =>
                    row.OwnerId == corporationId && row.FlagId == CorporationStorageRules.MarketDeliveriesFlag && row.Quantity > 0
                )
                .AsEnumerable()
                .Where(row => search is null || MatchesAssetSearch(catalog, search, row.TypeId, row.Quantity))
                .GroupBy(row => row.LocationId)
                .Select(group =>
                    catalog.FindStation(group.Key) is { } station
                        ? new CorporationDeliveryAssetLocation(station.Id, station.SolarSystemId, station.TypeId, group.Count())
                        : null
                )
                .OfType<CorporationDeliveryAssetLocation>()
                .OrderBy(location => location.SystemId)
                .ThenBy(location => location.StationId)
                .ToArray();
        });
    }

    public IReadOnlyList<InventoryItem> DeliveryAssets(long actorId, int corporationId, int stationId)
    {
        var live =
            LiveWorld.Find(database.Path)
            ?? throw new InvalidDataException("Corporation delivery asset reports require the resident world.");
        return live.Economy.Inspect<IReadOnlyList<InventoryItem>>(world =>
        {
            if (!CanReadDeliveryAssetReport(world, actorId, corporationId))
            {
                throw new InvalidDataException("Only corporation directors and accountants can view Market Deliveries reports.");
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (catalog.FindStation(stationId) is null)
            {
                return [];
            }
            return world
                .Filter<OwnedItem>(row =>
                    row.OwnerId == corporationId
                    && row.LocationId == stationId
                    && row.FlagId == CorporationStorageRules.MarketDeliveriesFlag
                    && row.Quantity > 0
                )
                .OrderBy(row => row.Id)
                .AsEnumerable()
                .Select(row => PilotInventoryStore.Read(catalog, row))
                .ToArray();
        });
    }

    private static bool CanReadDeliveryAssetReport(WorldAction world, long actorId, int corporationId)
    {
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (corporation.Id != corporationId)
        {
            throw new InvalidDataException("Read assets for your current corporation.");
        }
        return CorporationStorageRules.CanReadOfficeAssetReport(CorporationRoleStore.Effective(world, actorId, corporation));
    }

    private static void CheckAssetSearch(CorporationOfficeAssetSearch search)
    {
        if (search.CategoryId < 0 || search.GroupId < 0 || search.TypeId < 0 || search.MinimumQuantity < 0)
        {
            throw new InvalidDataException("Invalid corporation asset filter.");
        }
    }

    private static bool MatchesAssetSearch(CatalogDbContext catalog, CorporationOfficeAssetSearch search, int typeId, int quantity)
    {
        if (quantity < search.MinimumQuantity || search.TypeId != 0 && search.TypeId != typeId)
        {
            return false;
        }
        return search.GroupId == 0 && search.CategoryId == 0
            || catalog.FittingType(typeId) is { } type
                && (search.GroupId == 0 || type.GroupId == search.GroupId)
                && (search.CategoryId == 0 || type.CategoryId == search.CategoryId);
    }
}
