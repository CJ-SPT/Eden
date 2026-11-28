using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Ships;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryShopForPvpShip(EconomyTransaction world)
    {
        // Review once on an ordinary stop. Returning empty goes back to work before trying another supplier.
        if (Work.Journey?.Kind == TrafficJourneyKind.PvpShopping)
        {
            return false;
        }
        if (ammunitionSupply.FindPvpSupplier(world, _operation.Catalog, database.Traffic, Pilot, PvpShoppingThreats(), Now) is not { } plan)
        {
            return false;
        }
        BeginPvpShoppingLeg(Pilot.StationId, plan.Route, plan.Fit);
        return true;
    }

    private bool FinishPvpShopping(EconomyTransaction world)
    {
        if (
            Work.Journey is not { Kind: TrafficJourneyKind.PvpShopping } shopping
            || Pilot.StationId != shopping.DestinationStationId
            || Pilot.StationId == shopping.HomeStationId
        )
        {
            return false;
        }

        var catalog = _operation.Catalog;
        var staging = catalog.RequireStation(shopping.HomeStationId);
        var avoided = PvpShoppingThreats();
        var route = ReturnRoute((HullClass)Pilot.ShipGroupId);
        if (route is null)
        {
            if (avoided.Count > 0)
            {
                Wait("Waiting for a safe return route from PvP shopping.");
                return true;
            }
            _work = Work with { Journey = null, Retreating = true };
            return false;
        }
        var selected = shopping.PvpShoppingFit;
        var fitRoute = route;
        if (selected is not null)
        {
            if (catalog.FittingType(selected.HullTypeId) is not { } fit)
            {
                fitRoute = null;
            }
            else if (!TrafficRules.RouteAllows((HullClass)fit.GroupId, CapsuleerIndustry.PvpRouteVisitsHighSecurity(catalog, route)))
            {
                fitRoute = ReturnRoute((HullClass)fit.GroupId);
            }
        }
        var purchase = fitRoute is null
            ? default
            : ammunitionSupply.TryBuyEarnedPvpShip(
                world,
                catalog,
                database.Traffic,
                Pilot,
                Now,
                staging.SolarSystemId,
                CapsuleerIndustry.PvpRouteVisitsHighSecurity(catalog, fitRoute),
                stagedShoppingFit: selected
            );
        _operation.MarketChanged |= purchase.MarketChanged;
        if (purchase.Boarded)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            ResetTradeHull();
            route = fitRoute!;
        }
        else if (selected is not null)
        {
            CapsuleerIndustry.DelayPvpSupplierReview(world, pilotId, Now);
        }
        BeginPvpShoppingLeg(staging.Id, route);
        return true;

        CapsuleerTrafficRoute? ReturnRoute(HullClass hull) =>
            CapsuleerTrafficRoutes.ToStation(catalog, Pilot.StationId, staging.Id, hull, avoided, policy: world.Settings.Capsuleers);
    }

    private HashSet<int> PvpShoppingThreats() =>
        (Work.Routine?.Threats ?? []).Where(threat => threat.ExpiresAt > Work.ActiveTicks).Select(threat => threat.SystemId).ToHashSet();

    private void BeginPvpShoppingLeg(int staging, CapsuleerTrafficRoute route, CapsuleerPvpFitGoal? fit = null)
    {
        _route = route;
        _work = Work with
        {
            Journey = new(
                TrafficJourneyKind.PvpShopping,
                staging,
                route.SourceStationId,
                route.DestinationStationId,
                route.Systems,
                Work.DwellSequence
            )
            {
                PvpShoppingFit = fit,
            },
            RouteIndex = 0,
            RouteDirection = 1,
            Resupply = null,
            TrafficLane = null,
        };
        LaunchActivity();
        BeginTravel();
    }
}
