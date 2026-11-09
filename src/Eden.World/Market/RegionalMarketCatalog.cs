using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Mining;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;

namespace Eden.World.Market;

internal sealed record RegionalProduct(
    int TypeId,
    InventoryCategory Category,
    IReadOnlyDictionary<int, decimal> Materials,
    int UnlockHours,
    int Quantity
);

internal sealed record RegionalStation(CatalogStationDefinition Station, RegionalMarketRole Role);

internal sealed class RegionalMarketCatalog
{
    private const int MerlinType = 603; // Audited build-3662168 starter production cohort.
    private const int MineralStock = 10000;
    private const int OreDemand = 20000;
    private const int EquipmentStock = 50;
    private const int AmmunitionStock = 5000;
    private const int HullStock = 5;
    private const decimal OreReferenceYield = 0.5m;
    private static readonly ConditionalWeakTable<CatalogProvider, RegionalMarketCatalog> Catalogs = new();

    internal static RegionalMarketCatalog Read(CatalogDbContext catalog) => Catalogs.GetValue(catalog.Definitions, _ => new(catalog));

    internal IReadOnlyList<RegionalProduct> Products { get; }
    internal IReadOnlyList<RegionalStation> Stations { get; }

    private RegionalMarketCatalog(CatalogDbContext catalog)
    {
        List<RegionalProduct> products = [];
        HashSet<int> minerals = [];
        foreach (int type in PilotIndustryStore.ProductTypes(catalog))
        {
            var item = catalog.RequireItem(type);
            if (
                item.CategoryId == (int)InventoryCategory.Ship
                && type != MerlinType
                && type != CapsuleerCommunityStore.VentureType
                && type != HaulingRules.BadgerType
            )
            {
                continue;
            }
            var recipe = catalog
                .Definitions.BlueprintsForProduct(type)
                .Select(catalog.Definitions.ManufacturingRecipe)
                .FirstOrDefault(r => r?.Materials is { Length: > 0 });
            var output = recipe?.Products?.FirstOrDefault(p => p.TypeID == type);
            if (
                recipe?.Materials is not { } inputs
                || output?.Quantity is not > 0
                || inputs.Any(m =>
                    m.TypeID is not > 0
                    || m.Quantity is not > 0
                    || catalog.FittingType(m.TypeID.Value)?.CategoryId != (int)InventoryCategory.Material
                )
            )
            {
                continue;
            }
            var materials = inputs.ToDictionary(
                m => checked((int)m.TypeID!.Value),
                m => (decimal)m.Quantity!.Value / output.Quantity.Value
            );
            minerals.UnionWith(materials.Keys);
            var category = (InventoryCategory)item.CategoryId;
            int unlock = type switch
            {
                CapsuleerCommunityStore.VentureType => RegionalMarketRules.FirstHullHours,
                MerlinType => RegionalMarketRules.CombatHullHours,
                HaulingRules.BadgerType => RegionalMarketRules.HaulerHours,
                _ => RegionalMarketRules.IndustryHours,
            };
            int quantity = category switch
            {
                InventoryCategory.Ship => HullStock,
                InventoryCategory.Charge => AmmunitionStock,
                _ => EquipmentStock,
            };
            products.Add(new(type, category, materials, unlock, quantity));
        }
        products.AddRange(
            minerals.Select(type => new RegionalProduct(
                type,
                InventoryCategory.Material,
                new Dictionary<int, decimal> { [type] = 1 },
                0,
                MineralStock
            ))
        );
        foreach (var item in catalog.Definitions.Items.Values.Where(i => i.CategoryId == AsteroidDefinitions.Category))
        {
            var definition = catalog.Read(CatalogData.Client.Types, (long)item.Id);
            var ore = catalog.Read(CatalogData.Client.Typematerials, (long)item.Id);
            if (
                definition?.PortionSize is not > 0
                || ore?.Materials is not { Length: > 0 } yields
                || ore.RandomizedMaterials is { Length: > 0 }
                || yields.Any(m =>
                    m.MaterialTypeID is not > 0 || m.Quantity is not > 0 || !minerals.Contains(checked((int)m.MaterialTypeID.Value))
                )
            )
            {
                continue;
            }
            products.Add(
                new(
                    item.Id,
                    InventoryCategory.Asteroid,
                    yields.ToDictionary(
                        m => checked((int)m.MaterialTypeID!.Value),
                        m => OreReferenceYield * m.Quantity!.Value / definition.PortionSize.Value
                    ),
                    0,
                    OreDemand
                )
            );
        }
        Products = products.AsReadOnly();
        var stations = catalog.Stations().Where(s => StationCapabilities.Has(catalog, s.OperationId, StationService.Market)).ToArray();
        var hubs = stations
            .GroupBy(s => s.RegionId)
            .Select(g =>
                g.OrderByDescending(s => catalog.Definitions.GatesInSystem(s.SolarSystemId).Length)
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
