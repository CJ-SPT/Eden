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

internal sealed record PvpShoppingPlan(CapsuleerTrafficRoute Route, CapsuleerPvpFitGoal Fit);

internal sealed partial class CapsuleerIndustry
{
    private static readonly TimeSpan PvpFitReviewInterval = TimeSpan.FromMinutes(5);
    private static readonly CatalogDefinition<int[]> s_tradeHubs = new(
        "capsuleer-shopping-trade-hubs",
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
        Func<int, IEnumerable<int>, bool>? allowedMilitiaFit = null,
        CapsuleerPvpFitGoal? stagedMilitiaFit = null,
        CapsuleerPvpFitGoal? stagedShoppingFit = null
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

        var goal = stagedShoppingFit ?? stagedMilitiaFit ?? organization.PvpFitGoal;
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
                if (stagingSystem is null && snapshot.Profiles.Count != 0)
                {
                    CapsuleerCorporations.SaveOrganization(
                        world,
                        id,
                        organization with
                        {
                            PvpFitReviewAt = now + PvpFitReviewInterval.Ticks,
                        }
                    );
                }
                return default;
            }
            organization = organization with { PvpFitGoal = goal };
            CapsuleerCorporations.SaveOrganization(world, id, organization);
        }

        var kit = goal.Kit();
        var parts = kit.Parts();
        if (!SupportedKit(catalog, kit, parts))
        {
            if (stagedShoppingFit is null)
            {
                CapsuleerCorporations.SaveOrganization(world, id, organization with { PvpFitGoal = null });
            }
            return default;
        }
        long budget = PvpAcquisitionBudget(world, id, agent.StartingCents);
        bool permitted = PvpRouteAllows(catalog, kit.HullTypeId, visitsHighSecurity);
        bool needsFittingSkills = false;
        bool qualified = permitted && PvpFitQualified(world, catalog, pilot, kit, parts, out needsFittingSkills);
        var purchases = qualified ? QuotePvpGoal(world, catalog, id, station, parts, budget) : null;
        if (
            purchases is null
            && owned is null
            && allowedMilitiaFit is null
            && stagedShoppingFit is null
            && budget > 0
            && organization.PvpFitReviewAt <= now
        )
        {
            var affordable = SelectSnapshotPvpGoal(
                world,
                catalog,
                snapshot,
                pilot,
                station,
                current: null,
                staging,
                visitsHighSecurity,
                affordableReadyBudget: budget
            );
            if (affordable?.Kit() is { } starter && QuotePvpGoal(world, catalog, id, station, starter.Parts(), budget) is { } quote)
            {
                kit = starter;
                purchases = quote;
            }
            else
            {
                organization = organization with { PvpFitReviewAt = now + PvpFitReviewInterval.Ticks };
                CapsuleerCorporations.SaveOrganization(world, id, organization);
            }
        }
        if (purchases is null)
        {
            if (stagedShoppingFit is not null)
            {
                return default;
            }
            bool boughtBook =
                permitted
                && !qualified
                && careerTraining.TrainForEquipment(
                    world,
                    catalog,
                    pilot,
                    parts.Select(part => part.TypeId).ToArray(),
                    now,
                    improveFittingResources: needsFittingSkills
                );
            return new(MarketChanged: boughtBook);
        }
        var bought = market.BuyBotRefitSupplies(world, catalog, id, station, purchases);
        if (bought.Trades.Sum(trade => (long)trade.Quantity) != purchases.Sum(purchase => (long)purchase.Quantity))
        {
            throw new InvalidDataException("PvP ship offers changed before purchase.");
        }
        long ship = BoardKit(world, catalog, pilot, kit, now, requireOnlineFit: true);
        var savedGoal = organization.PvpFitGoal;
        var savedKit = stagedShoppingFit is not null && owned is null ? savedGoal?.Kit() : null;
        var nextGoal =
            savedKit is not null
            && SupportedKit(catalog, savedKit, savedKit.Parts())
            && PvpFitProgress(catalog, savedKit.HullTypeId, savedKit.Slots.Select(slot => slot.TypeId))
                .CompareTo(PvpFitProgress(catalog, kit.HullTypeId, kit.Slots.Select(slot => slot.TypeId))) > 0
                ? savedGoal
                : SelectSnapshotPvpGoal(
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
                PvpSupplierReviewAt = 0,
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

    internal CapsuleerPvpFitGoal? MilitiaFitAtStation(
        EconomyTransaction world,
        CatalogDbContext catalog,
        TrafficSnapshot snapshot,
        DockedPilot pilot,
        int stationId,
        int targetSystem,
        Func<int, IEnumerable<int>, bool> allowedFit
    )
    {
        long id = pilot.PilotId;
        if (
            world.Set<SavedCapsuleerAgent>().Find(id) is not { } agent
            || !CapsuleerWorkHulls.CanLeaveShip(world, catalog, pilot)
            || !StationCapabilities.Has(catalog, catalog.RequireStation(stationId).OperationId, StationService.Market)
            || PvpAcquisitionBudget(world, id, agent.StartingCents) is not > 0
        )
        {
            return null;
        }
        return SelectSnapshotPvpGoal(
            world,
            catalog,
            snapshot,
            pilot,
            stationId,
            current: null,
            targetSystem,
            visitsHighSecurity: false,
            allowedFit,
            affordableReadyBudget: PvpAcquisitionBudget(world, id, agent.StartingCents)
        );
    }

    private static Ship? OwnedPvpShip(WorldAction world, long pilot, CapsuleerOrganization organization) =>
        organization.EarnedPvpShipId is long ship && world.Ships.Find(ship) is { } owned && owned.OwnerId == pilot ? owned : null;

    internal PvpShoppingPlan? FindPvpSupplier(
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
        if (owned is not null && owned.Id != pilot.ShipId || organization.PvpSupplierReviewAt > now)
        {
            return null;
        }

        var goal = organization.PvpFitGoal;
        if (goal is null && snapshot.Profiles.Count == 0)
        {
            return null;
        }
        long budget = PvpAcquisitionBudget(world, id, agent.StartingCents);
        if (goal is not null && budget <= 0)
        {
            return null;
        }
        var kit = goal?.Kit();
        var parts = kit?.Parts();
        bool qualified = Qualified(kit, parts);
        bool canUseAlternative = owned is null && budget > 0 && snapshot.Profiles.Count != 0;
        if (goal is not null && !qualified && !canUseAlternative)
        {
            return null;
        }
        var policy = world.Settings.Capsuleers;
        if (!policy.CanWork(catalog.Definitions, pilot.SystemId))
        {
            return null;
        }
        var neighbors = CapsuleerTrafficRoutes.Read(catalog, pilot.SystemId, policy).Routes;
        var hubs = catalog.Definition(s_tradeHubs, 0);
        var reachable = CapsuleerTrafficRoutes.Nearby(
            catalog,
            pilot.SystemId,
            (HullClass)pilot.ShipGroupId,
            TrafficJourneyRules.MaximumJumps,
            avoided
        );
        var routes = neighbors
            .SelectMany(route => new[] { route.SourceStationId, route.DestinationStationId })
            .Concat(hubs)
            .Distinct()
            .Where(station => station != pilot.StationId)
            .Select(catalog.RequireStation)
            .Where(station => !avoided.Contains(station.SolarSystemId) && policy.CanWork(catalog.Definitions, station.SolarSystemId))
            .Select(station => (Station: station.Id, Path: reachable.PathTo(station.SolarSystemId, draw: 0)))
            .Where(stop => stop.Path is not null)
            .Select(stop => new CapsuleerTrafficRoute(pilot.StationId, stop.Station, stop.Path!))
            .OrderBy(route => route.Systems.Length)
            .ThenBy(route => route.DestinationStationId);
        PvpShoppingPlan? fallback = null;
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
                organization = organization with { PvpFitGoal = goal };
                CapsuleerCorporations.SaveOrganization(world, id, organization);
                kit = goal.Kit();
                parts = kit.Parts();
                qualified = Qualified(kit, parts);
                if (budget <= 0 || !qualified && !canUseAlternative)
                {
                    return null;
                }
            }
            if (
                qualified
                && PvpRouteAllows(catalog, goal.HullTypeId, highSecurity)
                && QuotePvpGoal(world, catalog, id, route.DestinationStationId, parts!, budget) is not null
            )
            {
                return new(route, goal);
            }
            if (
                canUseAlternative
                && fallback is null
                && SelectSnapshotPvpGoal(
                    world,
                    catalog,
                    snapshot,
                    pilot,
                    route.DestinationStationId,
                    current: null,
                    pilot.SystemId,
                    highSecurity,
                    affordableReadyBudget: budget
                )
                    is { } alternative
            )
            {
                fallback = new(route, alternative);
                if (!qualified)
                {
                    return fallback;
                }
            }
        }
        if (fallback is null)
        {
            DelayPvpSupplierReview(world, id, now);
        }
        return fallback;

        bool Qualified(ReplacementKit? desired, ReplacementPart[]? requirements) =>
            budget > 0
            && desired is not null
            && requirements is not null
            && SupportedKit(catalog, desired, requirements)
            && PvpFitQualified(world, catalog, pilot, desired, requirements, out _);
    }

    internal static void DelayPvpSupplierReview(EconomyTransaction world, long pilot, long now)
    {
        var organization = CapsuleerCorporations.Organization(world, pilot);
        CapsuleerCorporations.SaveOrganization(world, pilot, organization with { PvpSupplierReviewAt = now + PvpFitReviewInterval.Ticks });
    }

    internal static bool PvpRouteVisitsHighSecurity(CatalogDbContext catalog, CapsuleerTrafficRoute route) =>
        route.Systems.Any(system =>
            TrafficRules.SecurityBand(catalog.Definition(CapsuleerProvisioningCatalog.Security, system)) == TrafficSecurityBand.High
        );

    private static long PvpAcquisitionBudget(EconomyTransaction world, long pilot, long openingCents) =>
        CapsuleerRefitFunding.Applies(world, pilot) ? long.MaxValue : WalletBalance(world, pilot) - openingCents;
}
