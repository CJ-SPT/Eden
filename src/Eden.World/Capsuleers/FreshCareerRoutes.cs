using Eden.Data.Catalog;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Docking;

namespace Eden.World.Capsuleers;

internal sealed record FreshCareerRoutes(CapsuleerTrafficRoute? Courier, CatalogAgent? Agent, CapsuleerTrafficRoute? Combat)
{
    private static readonly CatalogDefinition<FreshCareerRoutes> s_routes = new("capsuleer-rookie-career-routes", Load);

    internal static FreshCareerRoutes Read(CatalogDbContext catalog, int system) => catalog.Definition(s_routes, system);

    private static FreshCareerRoutes Load(CatalogDbContext catalog, int system)
    {
        if (!HighSecurity(catalog, system))
        {
            return new(null, null, null);
        }

        var stations = catalog.Definitions.StationsInSystem(system).OrderBy(station => station.Id).ToArray();
        var market = stations.Where(station => StationCapabilities.Has(catalog, station.OperationId, StationService.Market)).ToArray();
        int home = CapsuleerProvisioningCatalog.StarterIndustry(catalog, system).HomeStationId ?? market.FirstOrDefault()?.Id ?? 0;
        CapsuleerTrafficRoute? combat = home > 0 && catalog.BeltAnchors(system).Count > 0 ? new(home, home, [system]) : null;
        var corvetteCapacity = CapsuleerProvisioningCatalog
            .Origins(catalog)
            .Select(origin => catalog.Read(CatalogData.Client.Races, (long)origin.RaceId)?.ShipTypeID ?? 0)
            .Select(hull => hull > 0 ? catalog.FittingType((int)hull)?.Capacity ?? 0 : 0)
            .Where(capacity => capacity > 0)
            .DefaultIfEmpty()
            .Min();
        foreach (
            var agent in NpcDirectory
                .Agents(catalog)
                .Where(agent =>
                    agent.SystemId == system
                    && agent.Type == AgentType.Basic
                    && agent.Level == FreshCareerRules.DistributionLevel
                    && agent.DivisionId == MissionRules.DistributionDivision
                    && market.Any(station => station.Id == agent.StationId)
                )
        )
        {
            if (agent.StationId is not int source || !PilotMissionStore.StarterCourierProfiles(catalog, agent, corvetteCapacity).Any())
            {
                continue;
            }
            var sites = catalog
                .Definitions.GatesInSystem(system)
                .Where(gate => HighSecurity(catalog, gate.DestinationSystemId))
                .SelectMany(gate => catalog.Definitions.StationsInSystem(gate.DestinationSystemId))
                .Concat(stations)
                .Where(station => station.Id != source)
                .OrderBy(station => station.SolarSystemId == system)
                .ThenBy(station => station.OwnerId != agent.CorporationId)
                .ThenBy(station => station.Id);
            foreach (var destination in sites)
            {
                var outbound = CapsuleerTrafficRoutes.ToStation(
                    catalog,
                    source,
                    destination.Id,
                    HullClass.Corvette,
                    maximumJumps: FreshCareerRules.DeliveryJumps
                );
                if (
                    outbound is not null
                    && CapsuleerTrafficRoutes.ToStation(
                        catalog,
                        destination.Id,
                        source,
                        HullClass.Corvette,
                        maximumJumps: FreshCareerRules.DeliveryJumps
                    )
                        is not null
                )
                {
                    return new(outbound, agent, combat);
                }
            }
        }
        return new(null, null, combat);
    }

    private static bool HighSecurity(CatalogDbContext catalog, int system) =>
        catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus >= CrimewatchRules.HighSecurity;
}
