using System.Globalization;
using Eden.Data.Catalog;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Industry;
using Eden.World.Mining;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerOrigin(int RaceId, int BloodlineId, int SchoolId, int CorporationId);

internal sealed record CapsuleerSite(int SystemId, CapsuleerEligibility Eligibility, int? HomeStationId, int? DestinationStationId)
{
    internal int OreBelts { get; init; }
}

internal static class CapsuleerProvisioningCatalog
{
    internal static readonly CatalogDefinition<double> Security = new(
        "capsuleer-local-security",
        (catalog, system) => catalog.Read(CatalogData.Client.Solarsystemcontent, (long)system)?.Security ?? 1
    );
    private static readonly CatalogDefinition<CapsuleerOrigin[]> s_origins = new("capsuleer-origins", (catalog, _) => LoadOrigins(catalog));
    private static readonly CatalogDefinition<CapsuleerSite> s_miningSites = new("capsuleer-mining-site", MiningSite);
    private static readonly CatalogDefinition<CapsuleerSite> s_starterIndustry = new("starter-industry-site", StarterIndustrySite);
    internal static readonly CatalogDefinition<ILookup<int, int>> StarterOreSources = new("starter-ore-sources", ReadStarterOreSources);

    private sealed record MiningHulls(CapsuleerMiningHull[] Barges, CapsuleerMiningHull[] Exhumers);

    private static readonly CatalogDefinition<MiningHulls> s_miningHulls = new("capsuleer-mining-hulls", ReadMiningHulls);

    internal static CapsuleerOrigin[] Origins(CatalogDbContext catalog) => catalog.Definition(s_origins, 0);

    internal static CapsuleerSite Mining(CatalogDbContext catalog, int systemId) => catalog.Definition(s_miningSites, systemId);

    internal static CapsuleerSite StarterIndustry(CatalogDbContext catalog, int systemId) =>
        catalog.Definition(s_starterIndustry, systemId);

    internal static bool StarterIndustryServices(CatalogDbContext catalog, CatalogStationDefinition station) =>
        StationCapabilities.Has(catalog, station.OperationId, StationService.Market)
        && StationCapabilities.Has(catalog, station.OperationId, StationService.Manufacturing)
        && StationCapabilities.Has(catalog, station.OperationId, StationService.Reprocessing)
        && catalog.Read(CatalogData.Sde.NpcStations, (long)station.Id)
            is { ReprocessingEfficiency: > 0 and <= 1, ReprocessingStationsTake: >= 0 and <= 1 };

    private static CapsuleerSite StarterIndustrySite(CatalogDbContext catalog, int system)
    {
        if (
            catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus
                is not >= Eden.Gameplay.Combat.CrimewatchRules.HighSecurity
            || catalog.FittingType((int)MiningLaserType.Civilian) is null
        )
        {
            return new(system, CapsuleerEligibility.MissingEquipment, null, null);
        }
        var station = catalog
            .Definitions.StationsInSystem(system)
            .Where(station => StarterIndustryServices(catalog, station))
            .MinBy(station => station.Id);
        if (station is null || !ManufacturingCatalog.Recipes(catalog).TryGetValue(StarterIndustryRules.MinerBlueprint, out var recipe))
        {
            return new(system, CapsuleerEligibility.MissingEquipment, null, null);
        }
        var belts = catalog.CelestialsInSystem(system).Where(belt => belt.GroupId == AsteroidDefinitions.BeltGroup).ToArray();
        if (belts.Length == 0 || !belts.All(belt => ValidCounts(catalog, belt.Id)))
        {
            return new(system, CapsuleerEligibility.NoUsableOre, null, null);
        }
        var sources = catalog.Definition(StarterOreSources, system);
        return recipe.Materials.All(material => material.TypeId is > 0 and <= int.MaxValue && sources.Contains((int)material.TypeId.Value))
            ? new(system, CapsuleerEligibility.Eligible, station.Id, null) { OreBelts = belts.Length }
            : new(system, CapsuleerEligibility.NoUsableOre, null, null);
    }

    private static ILookup<int, int> ReadStarterOreSources(CatalogDbContext catalog, int system) =>
        catalog
            .CelestialsInSystem(system)
            .Where(belt => belt.GroupId == AsteroidDefinitions.BeltGroup)
            .SelectMany(belt => catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, (long)belt.Id) ?? [])
            .Where(pair => pair.Value > 0)
            .Select(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture))
            .Distinct()
            .Where(id => SupportedOre(catalog, id))
            .SelectMany(id =>
                (catalog.Read(CatalogData.Client.Typematerials, (long)id)?.Materials ?? [])
                    .Where(source => source.MaterialTypeID is > 0 and <= int.MaxValue)
                    .Select(source => (Ore: id, Material: (int)source.MaterialTypeID!.Value))
            )
            .ToLookup(source => source.Material, source => source.Ore);

    internal static bool BeltHasOre(CatalogDbContext catalog, long belt, IReadOnlyList<int> types) =>
        catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, belt) is { } counts
        && types.Any(type => counts.GetValueOrDefault(type.ToString(CultureInfo.InvariantCulture)) > 0);

    internal static bool HasMiningFleets(CatalogDbContext catalog) => catalog.Definition(s_miningHulls, 0).Barges.Length > 0;

    internal static CapsuleerMiningHull[] MiningRoster(CatalogDbContext catalog, CapsuleerSite site, long seed)
    {
        var hulls = catalog.Definition(s_miningHulls, 0);
        return CapsuleerMiningRules.Roster(
            site.OreBelts,
            CapsuleerProvisioningRules.Seed(seed, site.SystemId, CapsuleerCareer.Mining),
            hulls.Barges,
            hulls.Exhumers
        );
    }

    private static MiningHulls ReadMiningHulls(CatalogDbContext catalog, int _)
    {
        if (!MiningLaserCatalog.Supported(catalog, (int)MiningLaserType.StripMinerI, InventoryDefinitions.StripMinerGroup))
        {
            return new([], []);
        }
        CapsuleerMiningHull[] barges = [CapsuleerMiningHull.Covetor, CapsuleerMiningHull.Retriever, CapsuleerMiningHull.Procurer];
        CapsuleerMiningHull[] exhumers = [CapsuleerMiningHull.Hulk, CapsuleerMiningHull.Mackinaw, CapsuleerMiningHull.Skiff];
        return new(barges.Where(Available).ToArray(), exhumers.Where(Available).ToArray());

        bool Available(CapsuleerMiningHull hull) => catalog.FittingType((int)hull) is not null;
    }

    private static CapsuleerOrigin[] LoadOrigins(CatalogDbContext catalog)
    {
        var bloodlines = catalog.Records(CatalogData.Client.Bloodlines).ToArray();
        return catalog
            .Records(CatalogData.Client.Schools)
            .OrderBy(s => s.Key)
            .Where(s => s.Value.RaceID is > 0 && s.Value.CorporationID is > 0 && s.Value.StartingStations is { Length: > 0 })
            .Where(s => catalog.NpcCorporationName(checked((int)s.Value.CorporationID!.Value)) is not null)
            .Where(s =>
                catalog.Read(CatalogData.Client.Races, s.Value.RaceID!.Value) is { Skills: not null, ShipTypeID: > 0 } race
                && catalog.FittingType(checked((int)race.ShipTypeID.Value)) is not null
            )
            .Where(s => (s.Value.StartingStations ?? []).All(id => catalog.FindStation(id) is not null))
            .SelectMany(s =>
                bloodlines
                    .Where(b => b.Value.RaceID == s.Value.RaceID)
                    .OrderBy(b => b.Key)
                    .Select(b => new CapsuleerOrigin(
                        checked((int)s.Value.RaceID!.Value),
                        checked((int)b.Key),
                        checked((int)s.Key),
                        checked((int)s.Value.CorporationID!.Value)
                    ))
            )
            .ToArray();
    }

    private static CapsuleerSite MiningSite(CatalogDbContext catalog, int system)
    {
        var security = catalog.Read(CatalogData.Client.Solarsystemcontent, (long)system)?.Security;
        var station = catalog
            .Definitions.StationsInSystem(system)
            .Where(s => StationCapabilities.Has(catalog, s.OperationId, StationService.Market))
            .OrderBy(s => s.Id)
            .FirstOrDefault();
        var belts =
            security >= CapsuleerProvisioningRules.MinimumSecurity && station is not null
                ? catalog.CelestialsInSystem(system).Where(b => b.GroupId == AsteroidDefinitions.BeltGroup).ToArray()
                : [];
        int oreBelts = belts.All(b => ValidCounts(catalog, b.Id)) ? belts.Count(b => UsableOre(catalog, b.Id)) : 0;
        bool ore = oreBelts > 0;
        bool equipment =
            catalog.FittingType(CapsuleerCommunityStore.VentureType) is not null
            && catalog.FittingType((int)MiningLaserType.MinerI) is not null;
        bool exportRoute =
            ore
            && catalog
                .Definitions.GatesInSystem(system)
                .Any(g =>
                    g.DestinationSystemId != system
                    && catalog.Definitions.Gates.TryGetValue(g.DestinationId, out var back)
                    && back.DestinationId == g.Id
                    && catalog.Read(CatalogData.Client.Solarsystemcontent, (long)g.DestinationSystemId)?.Security
                        >= CapsuleerProvisioningRules.MinimumSecurity
                    && catalog
                        .Definitions.StationsInSystem(g.DestinationSystemId)
                        .Any(s => StationCapabilities.Has(catalog, s.OperationId, StationService.Market))
                );
        var eligibility = CapsuleerProvisioningRules.Eligibility(
            new(true, security, station is not null, belts.Length > 0, ore, exportRoute, true, equipment)
        );
        return new CapsuleerSite(system, eligibility, station?.Id, null) { OreBelts = oreBelts };
    }

    internal static bool UsableOre(CatalogDbContext catalog, int belt)
    {
        var counts = catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, (long)belt);
        return counts is not null
            && counts.Any(pair =>
                pair.Value > 0
                && int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                && SupportedOre(catalog, id)
            );
    }

    private static bool ValidCounts(CatalogDbContext catalog, int belt)
    {
        var counts = catalog.Read(CatalogData.Client.Asteroidcountbytypeidbeltid, (long)belt);
        return counts is not null
            && counts.Values.All(c => c >= 0)
            && counts.Values.Sum() <= AsteroidDefinitions.IdsPerBelt
            && counts.All(pair =>
                int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int id)
                && (
                    pair.Value == 0
                    || catalog.FittingType(id) is { CategoryId: AsteroidDefinitions.Category }
                        && catalog.Read(CatalogData.Client.Types, (long)id)?.Volume is double volume
                        && double.IsFinite(volume)
                        && volume > 0
                )
            );
    }

    private static readonly CatalogDefinition<int> s_freight = new(
        "capsuleer-freight-ore",
        (catalog, _) =>
            catalog
                .Definitions.Items.Values.Where(i => i.CategoryId == AsteroidDefinitions.Category)
                .Select(i => i.Id)
                .Order()
                .FirstOrDefault(id => SupportedOre(catalog, id))
    );

    internal static int FreightOre(CatalogDbContext catalog) => catalog.Definition(s_freight, 0);

    internal static bool SupportedOre(CatalogDbContext catalog, int id) =>
        catalog.FittingType(id) is { CategoryId: AsteroidDefinitions.Category } type
        && Enum.IsDefined((MiningResourceGroup)type.GroupId)
        && catalog.Read(CatalogData.Client.Types, (long)id) is { Volume: > 0, BasePrice: > 0 } ore
        && double.IsFinite(ore.Volume!.Value)
        && ore.Volume <= AsteroidDefinitions.MinimumOreVolume
        && double.IsFinite(ore.BasePrice!.Value)
        && ore.PortionSize is > 0;
}
