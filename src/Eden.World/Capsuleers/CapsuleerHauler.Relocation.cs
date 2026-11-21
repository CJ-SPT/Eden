using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryCorporationRelocation()
    {
        if (_operation.World is not EconomyTransaction world || CapsuleerCorporations.Organization(world, pilotId).Relocation is null)
        {
            return false;
        }
        if (Work.Freight is { Stacks.Length: > 0 } freight)
        {
            if (freight.Destination != Pilot.StationId || Work.RegionalEscort is not null || Work.CorporationSupply is not null)
            {
                return false;
            }
            CapsuleerFreight.Deliver(world, pilotId, Pilot.ShipId, freight);
            _work = Work with { Freight = null };
        }
        if (CapsuleerCorporations.StartRelocation(world, _operation.Catalog, Pilot, Now) is not { } route)
        {
            return false;
        }
        _route = route;
        _work = Work with
        {
            Journey = new(
                TrafficJourneyKind.CorporationRelocation,
                route.SourceStationId,
                route.SourceStationId,
                route.DestinationStationId,
                route.Systems,
                Work.DwellSequence
            ),
            RouteIndex = 0,
            RouteDirection = 1,
            TrafficLane = null,
            ArrivalAt = null,
            ArrivalGateId = null,
            DepartureAt = null,
        };
        LaunchActivity();
        BeginTravel();
        return true;
    }

    private void FinishCorporationRelocation()
    {
        if (
            Work.Journey?.Kind == TrafficJourneyKind.CorporationRelocation
            && _operation.World is EconomyTransaction world
            && CapsuleerCorporations.FinishRelocation(world, _operation.Catalog, Pilot, Now)
        )
        {
            _work = Work with { Journey = null, Freight = null };
        }
    }
}
