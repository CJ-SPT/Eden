using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Eden.Data.Catalog;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Agents;
using Eden.World.Catalog;
using Eden.World.Docking;

namespace Eden.World.Capsuleers;

internal sealed record FreshCareerRoutes(CapsuleerTrafficRoute? Courier, CatalogAgent? Agent, CapsuleerTrafficRoute? Combat)
{
    internal CapsuleerTrafficRoute? Return { get; init; }
    private static readonly CatalogDefinition<int[]> s_frontierStations = new("fresh-frontier-stations", LoadFrontierStations);
    private static readonly CatalogDefinition<FreshCareerRoutes> s_routes = new(
        "capsuleer-rookie-career-routes",
        (catalog, system) => Load(catalog, system)
    );
    private static readonly ConditionalWeakTable<
        CapsuleerPopulationPolicy,
        ConcurrentDictionary<(long Catalog, int System), FreshCareerRoutes>
    > s_restrictedRoutes = new();

    internal static FreshCareerRoutes Read(CatalogDbContext catalog, int system, CapsuleerPopulationPolicy? policy = null)
    {
        if (policy is null || policy.Unrestricted())
        {
            return catalog.Definition(s_routes, system);
        }
        return s_restrictedRoutes
            .GetOrCreateValue(policy)
            .GetOrAdd((catalog.Definitions.Identity.Generation, system), _ => Load(catalog, system, policy));
    }

    internal static CapsuleerTrafficRoute? FrontierPatrol(
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerPopulationPolicy policy,
        bool canEnterNullSecurity,
        IReadOnlySet<int> avoided
    )
    {
        double security = catalog.Definitions.SystemSecurity.GetValueOrDefault(pilot.SystemId, 1);
        if (security <= 0 || (security < CrimewatchRules.HighSecurity && !canEnterNullSecurity))
        {
            return null;
        }
        int jumps = FreshCareerRules.MaximumFrontierJumps;
        return catalog
            .Definition(s_frontierStations, pilot.SystemId)
            .Where(station =>
            {
                double destination = catalog.Definitions.SystemSecurity.GetValueOrDefault(catalog.RequireStation(station).SolarSystemId, 1);
                return destination < security && (destination > 0 || canEnterNullSecurity);
            })
            .Select(station =>
                CapsuleerTrafficRoutes.ToStation(catalog, pilot.StationId, station, (HullClass)pilot.ShipGroupId, avoided, jumps, policy)
            )
            .OfType<CapsuleerTrafficRoute>()
            .Where(route =>
                canEnterNullSecurity
                || route.Systems.Skip(1).All(system => catalog.Definitions.SystemSecurity.GetValueOrDefault(system) > 0)
            )
            .OrderBy(route => route.Systems.Skip(1).Count(system => !HighSecurity(catalog, system)))
            .ThenBy(route => route.Systems.Length)
            .ThenBy(route => route.DestinationStationId)
            .FirstOrDefault();
    }

    private static int[] LoadFrontierStations(CatalogDbContext catalog, int origin) =>
        CapsuleerIndustry
            .NearbySystems(catalog, origin, FreshCareerRules.MaximumFrontierJumps)
            .Where(system => system != origin && !HighSecurity(catalog, system) && catalog.BeltAnchors(system).Count > 0)
            .SelectMany(system => catalog.Definitions.StationsInSystem(system))
            .Where(station => StationCapabilities.Has(catalog, station.OperationId, StationService.Market))
            .Select(station => station.Id)
            .ToArray();

    private static FreshCareerRoutes Load(CatalogDbContext catalog, int system, CapsuleerPopulationPolicy? policy = null)
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
                .Where(station => station.Id != source && policy?.CanWork(catalog.Definitions, station.SolarSystemId) != false)
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
                        is { } returning
                )
                {
                    return new(outbound, agent, combat) { Return = returning };
                }
            }
        }
        return new(null, null, combat);
    }

    private static bool HighSecurity(CatalogDbContext catalog, int system) =>
        catalog.Read(CatalogData.Client.Systems, (long)system)?.SecurityStatus >= CrimewatchRules.HighSecurity;
}
