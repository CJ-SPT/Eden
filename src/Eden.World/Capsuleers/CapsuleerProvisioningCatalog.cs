using System.Globalization;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.World.Catalog;
using Eden.World.Docking;

namespace Eden.World.Capsuleers;

internal sealed record CapsuleerOrigin(int RaceId, int BloodlineId, int SchoolId, int CorporationId);

internal sealed record CapsuleerSite(
    int SystemId,
    CapsuleerEligibility Eligibility,
    int? HomeStationId,
    int? DestinationStationId,
    bool SupportsMining = true
);

internal static class CapsuleerProvisioningCatalog
{
    internal static CapsuleerOrigin[] Origins(CatalogDbContext catalog)
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

    internal static CapsuleerSite[] Plan(
        CatalogDbContext catalog,
        bool enabled,
        IReadOnlyDictionary<int, int> establishedHomes,
        bool origins,
        IReadOnlySet<int>? selectedSystems = null
    )
    {
        bool equipment =
            catalog.FittingType(CapsuleerCommunityStore.VentureType) is not null
            && catalog.FittingType(HaulingRules.BadgerType) is not null
            && catalog.FittingType((int)MiningLaserType.MinerI) is not null;
        var selected = selectedSystems ?? catalog.Definitions.Systems.Keys.ToHashSet();
        var routeSystems = selected
            .SelectMany(system => catalog.Definitions.GatesInSystem(system).Select(g => g.DestinationSystemId))
            .Union(selected)
            .ToHashSet();
        var content = catalog.ReadMany(CatalogData.Client.Solarsystemcontent, routeSystems.Select(id => (long)id));
        var security = routeSystems.ToDictionary(id => id, id => content.GetValueOrDefault(id)?.Security);
        var markets = catalog
            .Stations()
            .Where(s => routeSystems.Contains(s.SolarSystemId))
            .Where(s => StationCapabilities.Has(catalog, s.OperationId, StationService.Market))
            .GroupBy(s => s.SolarSystemId)
            .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Id).ToArray());
        List<CapsuleerSite> sites = [];
        foreach (int system in selected.Order())
        {
            var stations = markets.GetValueOrDefault(system) ?? [];
            int? home = establishedHomes.TryGetValue(system, out int saved) ? saved : stations.FirstOrDefault()?.Id;
            bool market = home is not null && stations.Any(s => s.Id == home);
            bool belt = false;
            bool ore = false;
            int? destination = null;
            if (enabled && security[system] >= CapsuleerProvisioningRules.MinimumSecurity && market)
            {
                var belts = catalog.CelestialsInSystem(system).Where(b => b.GroupId == AsteroidDefinitions.BeltGroup).ToArray();
                belt = belts.Length != 0;
                ore = belts.All(b => ValidCounts(catalog, b.Id)) && belts.Any(b => UsableOre(catalog, b.Id));
                destination = catalog
                    .Definitions.GatesInSystem(system)
                    .Where(g => g.DestinationSystemId != system)
                    .OrderBy(g => g.DestinationSystemId)
                    .ThenBy(g => g.Id)
                    .Where(g => security.GetValueOrDefault(g.DestinationSystemId) >= CapsuleerProvisioningRules.MinimumSecurity)
                    .Where(g =>
                        catalog
                            .Definitions.GatesInSystem(g.DestinationSystemId)
                            .Any(exit => exit.Id == g.DestinationId && exit.DestinationId == g.Id && exit.DestinationSystemId == system)
                    )
                    .SelectMany(g => markets.GetValueOrDefault(g.DestinationSystemId) ?? [])
                    .Select(s => (int?)s.Id)
                    .FirstOrDefault();
            }
            var eligibility = CapsuleerProvisioningRules.Eligibility(
                new(enabled, security[system], market, belt, ore, destination is not null, origins, equipment)
            );
            if (eligibility == CapsuleerEligibility.NoBelt && destination is not null && origins && equipment)
            {
                eligibility = CapsuleerEligibility.Eligible;
            }
            sites.Add(new(system, eligibility, home, destination, belt));
        }
        return sites.ToArray();
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

    internal static bool SupportedOre(CatalogDbContext catalog, int id) =>
        catalog.FittingType(id) is { CategoryId: AsteroidDefinitions.Category } type
        && Enum.IsDefined((MiningResourceGroup)type.GroupId)
        && catalog.Read(CatalogData.Client.Types, (long)id) is { Volume: > 0, BasePrice: > 0 } ore
        && double.IsFinite(ore.Volume!.Value)
        && ore.Volume <= AsteroidDefinitions.MinimumOreVolume
        && double.IsFinite(ore.BasePrice!.Value)
        && ore.PortionSize is > 0;
}
