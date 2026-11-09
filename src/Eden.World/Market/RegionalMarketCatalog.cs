using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;

namespace Eden.World.Market;

internal sealed record RegionalProduct(int TypeId, InventoryCategory Category, int Quantity);

internal sealed record RegionalStation(CatalogStationDefinition Station, RegionalMarketRole Role);

internal sealed class RegionalMarketCatalog
{
    private static readonly ConditionalWeakTable<CatalogProvider, RegionalMarketCatalog> Catalogs = new();

    internal static RegionalMarketCatalog Read(CatalogDbContext catalog) => Catalogs.GetValue(catalog.Definitions, _ => new(catalog));

    internal IReadOnlyList<RegionalStation> Stations { get; }

    internal static RegionalProduct? Product(CatalogDbContext catalog, int typeId)
    {
        var item = catalog.FittingType(typeId);
        var definition = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (
            item is null
            || !RegionalMarketRules.CanUnlock(
                (InventoryCategory)item.CategoryId,
                item.GroupId,
                definition?.Published == 1,
                definition?.MarketGroupID is > 0
            )
            || !catalog.Definitions.BlueprintsForProduct(typeId).Any()
        )
        {
            return null;
        }
        var category = (InventoryCategory)item.CategoryId;
        return new(typeId, category, RegionalMarketRules.InitialStock(category));
    }

    private RegionalMarketCatalog(CatalogDbContext catalog)
    {
        var stations = catalog
            .Stations()
            .Where(s =>
                (
                    catalog.Read(CatalogData.Client.Systems, (long)s.SolarSystemId)?.SecurityStatus
                    ?? catalog.Read(CatalogData.Client.Solarsystemcontent, (long)s.SolarSystemId)?.Security
                ) >= CrimewatchRules.HighSecurity
                && StationCapabilities.Has(catalog, s.OperationId, StationService.Market)
            )
            .ToArray();
        var hubs = stations
            .GroupBy(s => s.RegionId)
            .Select(g =>
                g.OrderByDescending(s => CapsuleerEncounterRules.IsTradeHubStation(s.Id))
                    .ThenByDescending(s => catalog.Definitions.GatesInSystem(s.SolarSystemId).Length)
                    .ThenByDescending(s => catalog.Definitions.StationsInSystem(s.SolarSystemId).Length)
                    .ThenBy(s => s.Id)
                    .First()
                    .Id
            )
            .ToHashSet();
        Stations = stations
            .Select(s => new RegionalStation(
                s,
                hubs.Contains(s.Id) ? RegionalMarketRole.Hub
                    : StationCapabilities.Has(catalog, s.OperationId, StationService.Manufacturing) ? RegionalMarketRole.Industry
                    : RegionalMarketRole.Mining
            ))
            .ToArray();
    }
}
