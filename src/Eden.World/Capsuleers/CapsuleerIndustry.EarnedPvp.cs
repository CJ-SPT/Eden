using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static readonly CatalogDefinition<int[]> s_pvpTradeHubs = new(
        "pvp-shopping-trade-hubs",
        (catalog, _) => catalog.Definitions.Stations.Keys.Where(CapsuleerEncounterRules.IsTradeHubStation).ToArray()
    );

    internal static bool RequiresEarnedPvp(WorldAction world) => !world.Settings.FreshUniverse && world.Settings.Capsuleers.EarnedPvpShips;

    internal static bool HasPvpShip(WorldAction world, long pilot, long ship) =>
        !RequiresEarnedPvp(world) || CapsuleerCorporations.Organization(world, pilot).EarnedPvpShipId == ship;

    internal static bool CanStagePvp(WorldAction world, CatalogDbContext catalog, int system)
    {
        var policy = world.Settings.Capsuleers;
        double security = catalog.Definition(CapsuleerProvisioningCatalog.Security, system);
        return policy.RoamPercent > 0 && PatrolRules.Allowed(new(PatrolKind.Roam, system), security, policy)
            || policy.CampPercent > 0 && policy.CampSeconds > 0 && PatrolRules.Allowed(new(PatrolKind.GateCamp, system), security, policy);
    }

    internal CareerShipTurn TryBuyEarnedPvpShip(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        long now,
        int? stagingSystem = null,
        bool visitsHighSecurity = false
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        if (
            !RequiresEarnedPvp(world)
            || world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, pilot)
            || !CanStagePvp(world, catalog, stagingSystem ?? pilot.SystemId)
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
        )
        {
            return default;
        }

        var organization = CapsuleerCorporations.Organization(world, id);
        if (organization.EarnedPvpShipId is long ownedId && world.Ships.Find(ownedId) is { } owned && owned.OwnerId == id)
        {
            // Leave an existing fit at its station if the pilot cannot reach it from this hangar.
            if (
                owned.Id == pilot.ShipId
                || owned.Position is not null
                || owned.StationId != station
                || owned.StructureId.HasValue
                || owned.StoredInId.HasValue
                || !TrafficRules.RouteAllows((HullClass)catalog.RequireItem(owned.TypeId).GroupId, visitsHighSecurity)
            )
            {
                return default;
            }
            CapsuleerWorkHulls.Board(world, catalog, pilot, owned, now);
            CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpWorkShipId = pilot.ShipId });
            return new(Boarded: true);
        }

        long budget = StarterBalance(world, id) - agent.StartingCents;
        // The opening grant remains available for the pilot's work. Buy the whole combat kit in one market transaction.
        if (
            budget <= 0
            || QuoteSnapshotPvpFit(world, catalog, snapshot, pilot, station, budget, stagingSystem, visitsHighSecurity) is not { } quote
        )
        {
            return default;
        }
        var bought = market.ExecuteBuy(world, catalog, id, station, quote.Purchases, 0);
        if (bought.Trades.Sum(trade => (long)trade.Quantity) != quote.Purchases.Sum(purchase => (long)purchase.Quantity))
        {
            throw new InvalidDataException("PvP ship offers changed before purchase.");
        }
        long ship = BoardKit(world, catalog, pilot, quote.Kit, now, requireOnlineFit: true);
        CapsuleerCorporations.SaveOrganization(world, id, organization with { EarnedPvpShipId = ship, PvpWorkShipId = pilot.ShipId });
        return new(Boarded: true, MarketChanged: true);
    }

    internal CapsuleerTrafficRoute? FindPvpSupplier(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        IReadOnlySet<int> avoided
    )
    {
        long id = pilot.PilotId;
        if (
            !world.Settings.Economy.StandardMarket
            || snapshot.Profiles.Count == 0
            || world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent
            || StarterBalance(world, id) - agent.StartingCents is not > 0
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, pilot)
            || CapsuleerCorporations.Organization(world, id).EarnedPvpShipId is long ship && world.Ships.Find(ship) is not null
        )
        {
            return null;
        }

        long budget = StarterBalance(world, id) - agent.StartingCents;
        var policy = world.Settings.Capsuleers;
        var neighbors = CapsuleerTrafficRoutes.Read(catalog, pilot.SystemId, policy).Routes;
        var hubs = catalog.Definition(s_pvpTradeHubs, 0);
        return neighbors
            .SelectMany(route => new[] { route.SourceStationId, route.DestinationStationId })
            .Concat(hubs)
            .Distinct()
            .Where(station => station != pilot.StationId)
            .Select(station =>
                CapsuleerTrafficRoutes.ToStation(catalog, pilot.StationId, station, (HullClass)pilot.ShipGroupId, avoided, policy: policy)
            )
            .OfType<CapsuleerTrafficRoute>()
            .OrderBy(route => route.Systems.Length)
            .ThenBy(route => route.DestinationStationId)
            .FirstOrDefault(route =>
                QuoteSnapshotPvpFit(
                    world,
                    catalog,
                    snapshot,
                    pilot,
                    route.DestinationStationId,
                    budget,
                    pilot.SystemId,
                    PvpRouteVisitsHighSecurity(catalog, route)
                )
                    is not null
            );
    }

    internal static bool PvpRouteVisitsHighSecurity(CatalogDbContext catalog, CapsuleerTrafficRoute route) =>
        route.Systems.Any(system =>
            TrafficRules.SecurityBand(catalog.Definition(CapsuleerProvisioningCatalog.Security, system)) == TrafficSecurityBand.High
        );
}
