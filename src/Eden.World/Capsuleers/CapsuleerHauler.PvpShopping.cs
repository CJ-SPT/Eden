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
        var avoided = (Work.Routine?.Threats ?? []).Where(threat => threat.ExpiresAt > Work.ActiveTicks).Select(threat => threat.SystemId);
        if (ammunitionSupply.FindPvpSupplier(world, _operation.Catalog, database.Traffic, Pilot, avoided.ToHashSet(), Now) is not { } route)
        {
            return false;
        }
        BeginPvpShoppingLeg(Pilot.StationId, route);
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
        var route = CapsuleerTrafficRoutes.ToStation(
            catalog,
            Pilot.StationId,
            staging.Id,
            (HullClass)Pilot.ShipGroupId,
            policy: world.Settings.Capsuleers
        );
        if (route is null)
        {
            _work = Work with { Journey = null, Retreating = true };
            return false;
        }
        var purchase = ammunitionSupply.TryBuyEarnedPvpShip(
            world,
            catalog,
            database.Traffic,
            Pilot,
            Now,
            staging.SolarSystemId,
            CapsuleerIndustry.PvpRouteVisitsHighSecurity(catalog, route)
        );
        _operation.MarketChanged |= purchase.MarketChanged;
        if (purchase.Boarded)
        {
            _pilot = ReadPilot(world, CapsuleerCommunityStore.MinerAccountId);
            ResetTradeHull();
        }
        BeginPvpShoppingLeg(staging.Id, route);
        return true;
    }

    private void BeginPvpShoppingLeg(int staging, CapsuleerTrafficRoute route)
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
            ),
            RouteIndex = 0,
            RouteDirection = 1,
            Resupply = null,
            TrafficLane = null,
        };
        LaunchActivity();
        BeginTravel();
    }
}
