using System.Globalization;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Docking;
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

    private sealed record MiningHulls(CapsuleerMiningHull[] Barges, CapsuleerMiningHull[] Exhumers);

    private static readonly CatalogDefinition<MiningHulls> s_miningHulls = new("capsuleer-mining-hulls", ReadMiningHulls);

    internal static CapsuleerOrigin[] Origins(CatalogDbContext catalog) => catalog.Definition(s_origins, 0);

    internal static CapsuleerSite Mining(CatalogDbContext catalog, int systemId) => catalog.Definition(s_miningSites, systemId);

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
