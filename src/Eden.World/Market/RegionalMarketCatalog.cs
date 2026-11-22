using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;

namespace Eden.World.Market;

internal sealed record RegionalProduct(int TypeId, InventoryCategory Category, int Quantity);

internal sealed record RegionalStation(CatalogStationDefinition Station, RegionalMarketRole Role, bool StartingStation = false)
{
    internal bool StarterSupply => StartingStation || Role == RegionalMarketRole.Hub;
}

internal sealed record RegionalDemand(int TypeId, InventoryCategory Category, bool RawResource, bool Material, bool RequiresUnlock);

internal sealed class RegionalMarketCatalog
{
    private static readonly ConditionalWeakTable<CatalogProvider, RegionalMarketCatalog> Catalogs = new();

    // Pinned client metaGroups: faction=4, Tech II=2, Tech III=14.
    private const int FactionMetaGroup = 4;
    private const int StructureFactionMetaGroup = 52;
    private const int TechTwoMetaGroup = 2;
    private const int TechThreeMetaGroup = 14;

    internal static RegionalMarketCatalog Read(CatalogDbContext catalog) => Catalogs.GetValue(catalog.Definitions, _ => new(catalog));

    internal IReadOnlyList<RegionalStation> Stations { get; }
    internal IReadOnlyDictionary<int, RegionalStation> StationsById { get; }
    internal IReadOnlyDictionary<int, RegionalStation> Hubs { get; }
    private readonly Lazy<IReadOnlyDictionary<int, RegionalDemand>> _demand;
    internal IReadOnlyDictionary<int, RegionalDemand> Demand => _demand.Value;
    internal bool DemandLoaded => _demand.IsValueCreated;
    private readonly Lazy<IReadOnlyDictionary<int, RegionalProduct>> _starterProducts;
    internal IReadOnlyDictionary<int, RegionalProduct> StarterProducts => _starterProducts.Value;
    private readonly Lazy<HashSet<int>> _manufactured;
    private readonly CatalogDefinition<RegionalDemand?> _typeDemand;

    internal RegionalDemand? DemandFor(CatalogDbContext catalog, int typeId) =>
        DemandLoaded ? Demand.GetValueOrDefault(typeId) : catalog.Definitions.Definition(_typeDemand, typeId);

    private static IReadOnlyDictionary<int, RegionalProduct> LoadStarterProducts(CatalogDbContext catalog)
    {
        HashSet<int> candidates = [];
        Queue<int> pending = new(StarterMarketRules.Types);
        while (pending.TryDequeue(out int typeId))
        {
            if (!candidates.Add(typeId))
            {
                continue;
            }
            foreach (int skill in catalog.Definitions.RequiredSkills(typeId).Keys)
            {
                if (catalog.FittingType(skill)?.CategoryId == (int)InventoryCategory.Skill)
                {
                    pending.Enqueue(skill);
                }
            }
        }
        var definitions = catalog.ReadMany(CatalogData.Client.Types, candidates.Select(id => (long)id));
        return candidates
            .Select(catalog.FittingType)
            .OfType<CatalogItemDefinition>()
            .Where(item =>
            {
                var definition = definitions.GetValueOrDefault(item.Id);
                int fallbackTech = definition?.MetaGroupID switch
                {
                    TechTwoMetaGroup => 2,
                    TechThreeMetaGroup => 3,
                    _ => 1,
                };
                int tech = (int)(
                    definition?.TechLevel ?? catalog.FittingAttributes(item.Id).GetValueOrDefault(DogmaAttributeId.TechLevel, fallbackTech)
                );
                return StarterMarketRules.CanSupply(
                        (InventoryCategory)item.CategoryId,
                        definition?.Published == 1,
                        definition?.MarketGroupID is > 0,
                        tech,
                        definition?.MetaGroupID is FactionMetaGroup or StructureFactionMetaGroup
                    ) && MarketSnapshotCatalog.Quotes.GetValueOrDefault(item.Id)?.SellCents is > 0;
            })
            .ToDictionary(
                item => item.Id,
                item => new RegionalProduct(
                    item.Id,
                    (InventoryCategory)item.CategoryId,
                    RegionalMarketRules.InitialStock((InventoryCategory)item.CategoryId)
                )
            );
    }

    private static HashSet<int> StartingStations(CatalogDbContext catalog, IReadOnlyList<CatalogStationDefinition> stations)
    {
        var systems = catalog
            .Records(CatalogData.Client.Schoolmap)
            .Where(entry => entry.Value.SchoolID.HasValue)
            .GroupBy(entry => entry.Value.SchoolID!.Value)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Value.SolarSystemID).Distinct().ToArray());
        return catalog
            .Records(CatalogData.Client.Schools)
            .SelectMany(entry =>
            {
                var school = entry.Value;
                var starting = school.StartingStations ?? [];
                if (school.IsStarterSpaceSchool == 1 && starting.Length == 1)
                {
                    return stations.Where(station => station.Id == starting[0]);
                }
                var candidates = systems.GetValueOrDefault(entry.Key) is [long system]
                    ? stations.Where(station => station.SolarSystemId == system && starting.Contains(station.Id)).ToArray()
                    : [];
                return candidates.Length == 1 ? candidates : [];
            })
            .Select(station => station.Id)
            .ToHashSet();
    }

    internal static RegionalProduct? Product(CatalogDbContext catalog, int typeId)
    {
        var item = catalog.FittingType(typeId);
        var definition = catalog.Read(CatalogData.Client.Types, (long)typeId);
        if (
            item is null
            || RegionalMarketRules.IsPlayerSuppliedIndustryInput(typeId, (InventoryCategory)item.CategoryId, item.GroupId)
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

    private IReadOnlyDictionary<int, RegionalDemand> LoadDemand(CatalogDbContext catalog)
    {
        int[] types = MarketSnapshotCatalog.Quotes.Values.Where(q => q.BuyCents.HasValue).Select(q => q.TypeId).ToArray();
        var definitions = catalog.ReadMany(CatalogData.Client.Types, types.Select(id => (long)id));
        var attributes = catalog.Definitions.AttributeSets(types.Where(id => definitions.GetValueOrDefault(id)?.TechLevel is null));
        return types
            .Select(catalog.FittingType)
            .OfType<CatalogItemDefinition>()
            .Select(item => ClassifyDemand(item, definitions.GetValueOrDefault(item.Id), attributes.GetValueOrDefault(item.Id)))
            .OfType<RegionalDemand>()
            .ToDictionary(d => d.TypeId);
    }

    private RegionalDemand? LoadTypeDemand(CatalogDbContext catalog, int typeId)
    {
        if (MarketSnapshotCatalog.Quotes.GetValueOrDefault(typeId)?.BuyCents is null || catalog.FittingType(typeId) is not { } item)
        {
            return null;
        }
        var definition = catalog.Read(CatalogData.Client.Types, (long)typeId);
        var attributes = definition?.TechLevel is null ? catalog.FittingAttributes(typeId) : null;
        return ClassifyDemand(item, definition, attributes);
    }

    private RegionalDemand? ClassifyDemand(
        CatalogItemDefinition item,
        ClientTypes? definition,
        IReadOnlyDictionary<DogmaAttributeId, double>? attributes
    )
    {
        var category = (InventoryCategory)item.CategoryId;
        bool raw =
            category == InventoryCategory.Asteroid
            || item.GroupId is (int)HoldCargoGroup.HarvestableCloud or (int)HoldCargoGroup.CompressedGas;
        bool material = category == InventoryCategory.Material;
        int fallbackTech = definition?.MetaGroupID switch
        {
            TechTwoMetaGroup => 2,
            TechThreeMetaGroup => 3,
            _ => 1,
        };
        int tech = (int)(definition?.TechLevel ?? attributes?.GetValueOrDefault(DogmaAttributeId.TechLevel, fallbackTech) ?? fallbackTech);
        bool requiresUnlock = !raw && !material && tech != 1;
        bool eligible = RegionalMarketRules.CanDemand(
            new(
                category,
                item.GroupId,
                definition?.Published == 1,
                definition?.MarketGroupID is > 0,
                definition?.Volume ?? 0,
                tech,
                definition?.MetaGroupID is FactionMetaGroup or StructureFactionMetaGroup,
                raw || material,
                Manufacturable: true
            )
        );
        return eligible && (!requiresUnlock || _manufactured.Value.Contains(item.Id))
            ? new(item.Id, category, raw, material, requiresUnlock)
            : null;
    }

    private RegionalMarketCatalog(CatalogDbContext catalog)
    {
        string path = catalog.Definitions.Identity.Path;
        _typeDemand = new("regional-market-demand", LoadTypeDemand);
        _manufactured = new(() =>
        {
            using CatalogDbContext reader = new(path);
            return ManufacturingCatalog.Recipes(reader).Values.Select(recipe => recipe.ProductTypeId).ToHashSet();
        });
        _demand = new(() =>
        {
            using CatalogDbContext reader = new(path);
            return LoadDemand(reader);
        });
        _starterProducts = new(() =>
        {
            using CatalogDbContext reader = new(path);
            return LoadStarterProducts(reader);
        });
        var allStations = catalog.Stations().ToArray();
        var startingStations = StartingStations(catalog, allStations);
        var systems = catalog.ReadMany(CatalogData.Client.Systems, allStations.Select(s => (long)s.SolarSystemId));
        var fallback = catalog.ReadMany(
            CatalogData.Client.Solarsystemcontent,
            allStations.Where(s => systems.GetValueOrDefault(s.SolarSystemId)?.SecurityStatus is null).Select(s => (long)s.SolarSystemId)
        );
        var operations = catalog.ReadMany(CatalogData.Client.Stationoperations, allStations.Select(s => (long)s.OperationId));
        var stations = catalog
            .Stations()
            .Where(s =>
                (systems.GetValueOrDefault(s.SolarSystemId)?.SecurityStatus ?? fallback.GetValueOrDefault(s.SolarSystemId)?.Security)
                    >= CrimewatchRules.HighSecurity
                && operations.GetValueOrDefault(s.OperationId)?.Services?.Contains((long)StationService.Market) == true
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
                    : RegionalMarketRole.Mining,
                startingStations.Contains(s.Id)
            ))
            .ToArray();
        StationsById = Stations.ToDictionary(station => station.Station.Id);
        Hubs = Stations.Where(station => station.Role == RegionalMarketRole.Hub).ToDictionary(station => station.Station.RegionId);
    }
}
