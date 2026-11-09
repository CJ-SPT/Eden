using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal static class CapsuleerTrafficFlight
{
    internal const double ApproachRange = 500;
    private const double OpeningApproachClearance = 12000;

    internal static HaulerProgress OpeningInbound(
        WorldDbContext world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerTrafficRoute route,
        WatchedTrafficGate gate,
        long elapsed,
        long now,
        bool sourceObserved = false
    )
    {
        NavigationState navigation = new();
        HaulerProgress work = new(
            sourceObserved ? HaulerActivity.Docked : HaulerActivity.Inbound,
            0,
            0,
            pilot.ShipId,
            route.DestinationStationId,
            0,
            navigation.Capture(),
            ActiveTicks: elapsed,
            ClockOffsetTicks: elapsed,
            TrafficLane: gate,
            LaneDeparting: true
        );
        if (sourceObserved)
        {
            return work;
        }
        var leg = HaulingRouteRules.Crossing(route.Systems, gate.SourceSystemId, gate.ObserverSystemId);
        var departure = SystemObjects.Geometry(catalog, gate.SourceSystemId).Single(o => o.Id == gate.DepartureGateId);
        var position = departure.Position + new SpacePosition(departure.Radius + pilot.Flight.ShipRadius + ApproachRange, 0, 0);
        SqliteWorld.SetPosition(world, catalog, pilot.ShipId, position, gate.SourceSystemId, now);
        int dwell = HaulingRules.InboundDwellSeconds(
            catalog.Definitions.GatesInSystem(gate.ObserverSystemId).Length,
            catalog.Definitions.StationsInSystem(gate.ObserverSystemId).Length,
            pilot.PilotId
        );
        return work with
        {
            RouteIndex = leg.Index,
            RouteDirection = leg.Direction,
            TargetId = gate.DepartureGateId,
            DueStamp = dwell,
            DueTicks = elapsed + TimeSpan.FromSeconds(dwell).Ticks,
            DwellSequence = 1,
        };
    }

    internal static CapsuleerTrafficRoute Route(SavedCapsuleerHauler saved, CatalogDbContext catalog)
    {
        int source = catalog.RequireStation(saved.SourceStationId).SolarSystemId;
        int destination = catalog.RequireStation(saved.DestinationStationId).SolarSystemId;
        return new(
            saved.SourceStationId,
            saved.DestinationStationId,
            saved.RouteSystems is { Length: > 0 } systems ? systems
                : source == destination ? [source]
                : [source, destination]
        );
    }

    internal static HaulerRouteLeg Leg(CapsuleerTrafficRoute route, HaulerProgress work, int systemId) =>
        HaulingRouteRules.Leg(
            route.Systems,
            systemId,
            work.RouteIndex,
            work.RouteDirection,
            route.SourceStationId == route.DestinationStationId
        );

    internal static StaticSpaceObject Target(
        CapsuleerTrafficRoute route,
        HaulerProgress work,
        int systemId,
        IReadOnlyList<StaticSpaceObject> objects
    )
    {
        var leg = Leg(route, work, systemId);
        int station = leg.Direction < 0 ? route.SourceStationId : route.DestinationStationId;
        return leg.NextSystemId is { } next
            ? objects.Where(o => o.DestinationSystemId == next).OrderBy(o => o.Id).FirstOrDefault()
                ?? throw new InvalidDataException("The saved hauling route has no connected stargate.")
            : objects.SingleOrDefault(o => o.Id == station)
                ?? throw new InvalidDataException("The saved hauling route has no reachable endpoint station.");
    }

    internal static HaulerProgress Opening(
        WorldDbContext world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerTrafficRoute route,
        int systemId,
        HaulerEncounterOpening opening,
        long elapsed,
        long now
    )
    {
        NavigationState navigation = new();
        HaulerProgress work = new(
            HaulerActivity.Docked,
            0,
            0,
            pilot.ShipId,
            route.DestinationStationId,
            0,
            navigation.Capture(),
            ActiveTicks: elapsed,
            ClockOffsetTicks: elapsed
        );
        if (opening == HaulerEncounterOpening.Docked && pilot.HomeSystemId == systemId)
        {
            return work;
        }
        var objects = SystemObjects.Geometry(catalog, systemId).ToArray();
        var leg = Leg(route, work, systemId);
        // A first encounter at an endpoint is arriving from its connected gate,
        // while a corridor encounter continues through toward the next leg.
        int direction = leg.Index == 0 ? -1 : leg.Direction;
        work = work with { RouteIndex = leg.Index, RouteDirection = direction };
        leg = Leg(route, work, systemId);
        var target = Target(route, work, systemId, objects);
        SpacePosition position;
        SpacePosition? launchVelocity = null;
        if (opening == HaulerEncounterOpening.ApproachingStation && target.DestinationSystemId is null)
        {
            position = target.Position + new SpacePosition(target.Radius + pilot.Flight.ShipRadius + OpeningApproachClearance, 0, 0);
        }
        else
        {
            if (route.Systems.Length == 1 && route.SourceStationId != route.DestinationStationId)
            {
                int departureId = leg.Direction < 0 ? route.DestinationStationId : route.SourceStationId;
                var departure = catalog.RequireStation(departureId);
                var departureBody = objects.Single(o => o.Id == departureId);
                var station = pilot.Home with
                {
                    Id = departure.Id,
                    TypeId = departure.TypeId,
                    OwnerId = departure.OwnerId,
                    OperationId = departure.OperationId,
                    OrbitId = departure.OrbitId,
                    Position = departureBody.Position,
                    UndockPoint = StationUndocking.SelectPoint(
                        pilot.ShipId,
                        pilot.ShipGroupId,
                        StationUndockCatalog.Read(catalog, departure.TypeId)
                    ),
                };
                var launch = StationUndocking.Launch(pilot.ShipId, station, pilot.Flight with { StationRadius = departureBody.Radius });
                position = launch.Position;
                launchVelocity = launch.Velocity;
                world.Ships.Find(pilot.ShipId)!.StationId = departureId;
            }
            else
            {
                int? previous =
                    leg.Index > 0 ? route.Systems[leg.Index - 1]
                    : route.Systems.Length > 1 ? route.Systems[1]
                    : null;
                var gates = objects.Where(o => o.DestinationGateId is not null && o.DestinationSystemId is not null).OrderBy(o => o.Id);
                var gate =
                    (previous is null ? null : gates.FirstOrDefault(o => o.DestinationSystemId == previous)) ?? gates.FirstOrDefault();
                position = gate is null
                    ? target.Position + new SpacePosition(target.Radius + pilot.Flight.ShipRadius + OpeningApproachClearance, 0, 0)
                    : GateTravelRules.Arrival(pilot.ShipId, gate.Position, gate.Radius, pilot.Flight.ShipRadius);
            }
        }
        SqliteWorld.SetPosition(world, catalog, pilot.ShipId, position, systemId, now);
        if (!navigation.StartWarp(pilot.ShipId, pilot.Flight, position, target, 0, 0))
        {
            navigation.Follow(pilot.ShipId, 0, position, pilot.Flight.ShipRadius, target, ApproachRange, false, true);
        }
        navigation.SetVelocity(launchVelocity ?? (target.Position - position).Unit * (pilot.Flight.MaxSpeed / 2));
        return work with
        {
            Activity = HaulerActivity.Flying,
            TargetId = target.Id,
            DestinationStationId = leg.Direction < 0 ? route.SourceStationId : route.DestinationStationId,
            Navigation = navigation.Capture(),
        };
    }
}
