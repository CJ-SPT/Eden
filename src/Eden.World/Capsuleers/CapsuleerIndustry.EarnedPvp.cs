using Eden.Data.World.Capsuleers;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerIndustry
{
    private static readonly TimeSpan PvpFitReviewInterval = TimeSpan.FromMinutes(5);
    private static readonly CatalogDefinition<int[]> s_pvpTradeHubs = new(
        "pvp-shopping-trade-hubs",
        (catalog, _) => catalog.Definitions.Stations.Keys.Where(CapsuleerEncounterRules.IsTradeHubStation).ToArray()
    );

    internal static bool RequiresEarnedPvp(WorldAction world) => world.Settings.Capsuleers.EarnedPvpShips;

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
        bool visitsHighSecurity = false,
        Func<int, IEnumerable<int>, bool>? allowedMilitiaFit = null
    )
    {
        long id = pilot.PilotId;
        int station = pilot.StationId;
        int staging = stagingSystem ?? pilot.SystemId;
        if (
            allowedMilitiaFit is null && (!RequiresEarnedPvp(world) || !CanStagePvp(world, catalog, staging))
            || world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, pilot)
            || !StationCapabilities.Has(catalog, pilot.OperationId, StationService.Market)
        )
        {
            return default;
        }

        var organization = CapsuleerCorporations.Organization(world, id);
        var owned = OwnedPvpShip(world, id, organization);
        if (
            owned is not null
            && allowedMilitiaFit is not null
            && !allowedMilitiaFit(owned.TypeId, world.FittedItems(id, owned.Id).Select(item => item.TypeId))
        )
        {
            owned = null;
        }
        if (owned is not null && owned.Id != pilot.ShipId)
        {
            if (
                owned.Position is not null
                || owned.StationId != station
                || owned.StructureId.HasValue
                || owned.StoredInId.HasValue
                || !PvpRouteAllows(catalog, owned.TypeId, visitsHighSecurity)
            )
            {
                return default;
            }
            CapsuleerWorkHulls.Board(world, catalog, pilot, owned, now);
            CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpWorkShipId = pilot.ShipId });
            return new(Boarded: true);
        }

        var goal = organization.PvpFitGoal;
        if (
            goal is not null
            && allowedMilitiaFit is not null
            && !allowedMilitiaFit(goal.HullTypeId, goal.Kit().Slots.Select(slot => slot.TypeId))
        )
        {
            goal = null;
        }
        if (goal is null)
        {
            if (stagingSystem is null && organization.PvpFitReviewAt > now)
            {
                return default;
            }
            goal = SelectSnapshotPvpGoal(world, catalog, snapshot, pilot, station, owned, staging, visitsHighSecurity, allowedMilitiaFit);
            if (goal is null)
            {
                return default;
            }
            organization = organization with { PvpFitGoal = goal };
            CapsuleerCorporations.SaveOrganization(world, id, organization);
        }

        var kit = goal.Kit();
        var parts = kit.Parts();
        if (!SupportedKit(catalog, kit, parts))
        {
            CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpFitGoal = null });
            return default;
        }
        if (!PvpRouteAllows(catalog, kit.HullTypeId, visitsHighSecurity))
        {
            return default;
        }
        if (!PvpFitQualified(world, catalog, pilot, kit, parts, out bool needsFittingSkills))
        {
            bool boughtBook = careerTraining.TrainForEquipment(
                world,
                catalog,
                pilot,
                parts.Select(part => part.TypeId).ToArray(),
                now,
                improveFittingResources: needsFittingSkills
            );
            return new(MarketChanged: boughtBook);
        }
        long budget = WalletBalance(world, id) - agent.StartingCents;
        if (QuotePvpGoal(world, catalog, id, station, parts, budget) is not { } purchases)
        {
            return default;
        }
        var bought = market.ExecuteBuy(world, catalog, id, station, purchases, 0);
        if (bought.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(purchase => (long)purchase.Quantity))
        {
            throw new InvalidDataException("PvP ship offers changed before purchase.");
        }
        long ship = BoardKit(world, catalog, pilot, kit, now, requireOnlineFit: true);
        var nextGoal = SelectSnapshotPvpGoal(
            world,
            catalog,
            snapshot,
            pilot,
            station,
            world.Ships.Find(ship),
            staging,
            visitsHighSecurity,
            allowedMilitiaFit
        );
        CapsuleerCorporations.SaveOrganization(
            world,
            id,
            organization with
            {
                EarnedPvpShipId = ship,
                PvpWorkShipId = owned is null ? pilot.ShipId : organization.PvpWorkShipId,
                PvpFitGoal = nextGoal,
                PvpFitReviewAt = 0,
            }
        );
        return new(Boarded: true, MarketChanged: true);
    }

    internal static bool PvpFitQualified(
        EconomyTransaction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        ReplacementKit kit,
        ReplacementPart[] parts,
        out bool needsFittingSkills
    )
    {
        needsFittingSkills = false;
        if (!CanReplaceKit(world, catalog, pilot.PilotId, CapsuleerCareer.Hauling, kit, parts))
        {
            return false;
        }
        needsFittingSkills = !PilotFittingStore.CanOnlineFit(world, catalog, pilot.PilotId, kit);
        return !needsFittingSkills;
    }

    private static Ship? OwnedPvpShip(WorldAction world, long pilot, CapsuleerOrganization organization) =>
        organization.EarnedPvpShipId is long ship && world.Ships.Find(ship) is { } owned && owned.OwnerId == pilot ? owned : null;

    internal CapsuleerTrafficRoute? FindPvpSupplier(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        IReadOnlySet<int> avoided,
        long now
    )
    {
        long id = pilot.PilotId;
        if (
            !world.Settings.Economy.StandardMarket
            || world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, pilot)
        )
        {
            return null;
        }
        var organization = CapsuleerCorporations.Organization(world, id);
        var owned = OwnedPvpShip(world, id, organization);
        if (owned is not null && owned.Id != pilot.ShipId)
        {
            return null;
        }

        var goal = organization.PvpFitGoal;
        if (goal is null && (snapshot.Profiles.Count == 0 || organization.PvpFitReviewAt > now))
        {
            return null;
        }
        long budget = WalletBalance(world, id) - agent.StartingCents;
        var kit = goal?.Kit();
        var parts = kit?.Parts();
        if (kit is not null && (budget <= 0 || !PvpFitQualified(world, catalog, pilot, kit, parts!, out _)))
        {
            return null;
        }
        var policy = world.Settings.Capsuleers;
        var neighbors = CapsuleerTrafficRoutes.Read(catalog, pilot.SystemId, policy).Routes;
        var hubs = catalog.Definition(s_pvpTradeHubs, 0);
        var routes = neighbors
            .SelectMany(route => new[] { route.SourceStationId, route.DestinationStationId })
            .Concat(hubs)
            .Distinct()
            .Where(station => station != pilot.StationId)
            .Select(station =>
                CapsuleerTrafficRoutes.ToStation(catalog, pilot.StationId, station, (HullClass)pilot.ShipGroupId, avoided, policy: policy)
            )
            .OfType<CapsuleerTrafficRoute>()
            .OrderBy(route => route.Systems.Length)
            .ThenBy(route => route.DestinationStationId);
        foreach (var route in routes)
        {
            bool highSecurity = PvpRouteVisitsHighSecurity(catalog, route);
            if (goal is null)
            {
                goal = SelectSnapshotPvpGoal(
                    world,
                    catalog,
                    snapshot,
                    pilot,
                    route.DestinationStationId,
                    owned,
                    pilot.SystemId,
                    highSecurity
                );
                if (goal is null)
                {
                    continue;
                }
                CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpFitGoal = goal });
                kit = goal.Kit();
                parts = kit.Parts();
                if (!PvpFitQualified(world, catalog, pilot, kit, parts, out _) || budget <= 0)
                {
                    return null;
                }
            }
            if (
                PvpRouteAllows(catalog, goal.HullTypeId, highSecurity)
                && QuotePvpGoal(world, catalog, id, route.DestinationStationId, parts!, budget) is not null
            )
            {
                return route;
            }
        }
        if (goal is null)
        {
            CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpFitReviewAt = now + PvpFitReviewInterval.Ticks });
        }
        return null;
    }

    internal static bool PvpRouteVisitsHighSecurity(CatalogDbContext catalog, CapsuleerTrafficRoute route) =>
        route.Systems.Any(system =>
            TrafficRules.SecurityBand(catalog.Definition(CapsuleerProvisioningCatalog.Security, system)) == TrafficSecurityBand.High
        );
}
