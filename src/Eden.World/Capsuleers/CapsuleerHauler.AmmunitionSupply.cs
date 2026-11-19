using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerHauler
{
    private bool TryCorporationSupply()
    {
        if (Live is not { } live || _operation.World is not EconomyTransaction world)
        {
            return false;
        }
        var catalog = _operation.Catalog;
        if (Work.CorporationSupply is null && Work.PlanetaryProduction is { } pending && pending.CorporationId != Pilot.CorpId)
        {
            _work = Work with { PlanetaryProduction = null, PlanetaryRetryAt = 0 };
        }
        if (Work.CorporationSupply is { } assigned)
        {
            if (assigned.FactorySupply is { } factory)
            {
                return HandleFactorySupply(world, live, assigned, factory);
            }
            if (Pilot.StationId == _route.SourceStationId)
            {
                if (Work.Freight is { } delivered)
                {
                    CapsuleerIndustry.Deliver(world, catalog, Pilot, assigned, delivered);
                }
                _work = Work with
                {
                    CorporationSupply = null,
                    Freight = null,
                    Journey = null,
                    DueTicks = Work.ActiveTicks + TimeSpan.TicksPerSecond,
                };
                return true;
            }
            if (Pilot.StationId == _route.DestinationStationId)
            {
                var freight = Work.Freight ?? ammunitionSupply.Purchase(world, catalog, Pilot, assigned, _route.SourceStationId, Now);
                _operation.MarketChanged |= Work.Freight is null && freight is not null;
                _work = Work with { Freight = freight, RouteDirection = -1 };
                LaunchActivity();
                BeginTravel();
                return true;
            }
            return false;
        }
        if (
            Pilot.CorpId < Eden.Gameplay.Corporations.CorporationRules.FirstCorporationId
            || InActiveGang
            || Work.RegionalEscort is not null
            || Work.Ratting is not null
            || Work.Retreating
            || Work.Freight is { Stacks.Length: > 0 } existing && existing.Destination != Pilot.StationId
        )
        {
            return false;
        }

        var avoided = AvoidedSupplierSystems();
        var collection =
            ammunitionSupply.PlanFactoryCollection(world, catalog, live, Pilot, avoided, Now, Work)
            ?? CapsuleerIndustry.PlanCollection(world, catalog, live, Pilot, avoided, Now);
        if (collection is null)
        {
            return false;
        }
        if (Work.Freight is { } arrived)
        {
            CapsuleerFreight.Deliver(world, pilotId, Pilot.ShipId, arrived);
        }
        _work = Work with { CorporationSupply = collection.Run, Freight = null, Resupply = null };
        if (collection.Run.FactorySupply is { } factoryRun && Pilot.StationId == collection.Route.DestinationStationId)
        {
            return HandleFactorySupply(world, live, collection.Run, factoryRun);
        }
        BeginSupplyJourney(collection.Route);
        return true;
    }

    private HashSet<int> AvoidedSupplierSystems() =>
        (Work.Routine?.Threats ?? [])
            .Where(observed => observed.ExpiresAt > Work.ActiveTicks)
            .Select(observed => observed.SystemId)
            .ToHashSet();

    private void BeginSupplyJourney(CapsuleerTrafficRoute route)
    {
        _route = route;
        _work = Work with
        {
            Journey = new(
                TrafficJourneyKind.Regional,
                Pilot.StationId,
                _route.SourceStationId,
                _route.DestinationStationId,
                _route.Systems,
                Work.Journey?.Sequence ?? 0
            ),
            RouteDirection = 1,
            TrafficLane = null,
        };
        LaunchActivity();
        BeginTravel();
    }
}
