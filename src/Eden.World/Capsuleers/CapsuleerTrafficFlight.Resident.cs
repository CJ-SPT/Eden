using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Capsuleers;

internal static partial class CapsuleerTrafficFlight
{
    internal static HaulerProgress OpeningInbound(
        WorldAction world,
        CatalogDbContext catalog,
        DockedPilot pilot,
        CapsuleerTrafficRoute route,
        WatchedTrafficGate gate,
        long elapsed,
        long now,
        bool sourceObserved = false,
        int visitor = 0
    )
    {
        NavigationState navigation = new();
        int cadence = HaulingRules.InboundDwellSeconds(
            catalog.Definitions.GatesInSystem(gate.ObserverSystemId).Length,
            catalog.Definitions.StationsInSystem(gate.ObserverSystemId).Length,
            Math.Min(gate.ArrivalGateId, gate.DepartureGateId)
        );
        int delay = HaulingRules.StationDwellSeconds(pilot.PilotId, 0) + visitor * cadence;
        HaulerProgress work = new(
            HaulerActivity.Docked,
            0,
            delay,
            pilot.ShipId,
            route.DestinationStationId,
            0,
            navigation.Capture(),
            ActiveTicks: elapsed,
            DueTicks: elapsed + TimeSpan.FromSeconds(delay).Ticks,
            ClockOffsetTicks: elapsed,
            TrafficLane: gate,
            Journey: new(
                route.Systems.Length - 1 >= TrafficJourneyRules.LongDistanceMinimumJumps
                    ? TrafficJourneyKind.LongDistance
                    : TrafficJourneyKind.Regional,
                route.SourceStationId,
                route.SourceStationId,
                route.DestinationStationId,
                route.Systems,
                0
            )
        );
        if (sourceObserved || visitor != 0)
        {
            return work;
        }
        // Seed one already-traveling visitor finishing its approach on the unseen side.
        // Remaining visitors wait at stations, so entering the source never reveals a parked queue.
        var leg = HaulingRouteRules.Crossing(route.Systems, gate.SourceSystemId, gate.ObserverSystemId);
        var departure = SystemObjects.Geometry(catalog, gate.SourceSystemId).Single(body => body.Id == gate.DepartureGateId);
        var direction = (
            GateTravelRules.Arrival(pilot.ShipId, departure.Position, departure.Radius, pilot.Flight.ShipRadius) - departure.Position
        ).Unit;
        var position = departure.Position + direction * (departure.Radius + pilot.Flight.ShipRadius + OpeningGateApproachRange);
        SqliteWorld.SetPosition(world, catalog, pilot.ShipId, position, gate.SourceSystemId, now);
        navigation.Follow(pilot.ShipId, 0, position, pilot.Flight.ShipRadius, departure, ApproachRange, false, true);
        navigation.SetVelocity((departure.Position - position).Unit * (pilot.Flight.MaxSpeed / 2));
        return work with
        {
            Activity = HaulerActivity.Flying,
            DueStamp = 0,
            DueTicks = elapsed + TimeSpan.TicksPerSecond,
            RouteIndex = leg.Index,
            RouteDirection = leg.Direction,
            TargetId = departure.Id,
            Navigation = navigation.Capture(),
        };
    }

    internal static HaulerProgress Opening(
        WorldAction world,
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
