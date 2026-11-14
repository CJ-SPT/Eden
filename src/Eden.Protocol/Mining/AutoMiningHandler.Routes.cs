using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Mining;

internal sealed partial class AutoMiningHandler
{
    private enum JourneyPurpose
    {
        Mining,
        Unload,
        Return,
    }

    private sealed record MiningJourney(
        AutoMiningDestination Destination,
        JourneyPurpose Purpose,
        Queue<AutoMiningGate> Gates,
        long Started,
        long? MovingTo = null
    );

    private AutoMiningRoute? _route;
    private AutoMiningDestination? _destination;
    private MiningJourney? _journey;
    private Queue<AutoMiningDestination> _routeStops = new();
    private int? _expectedSystem;
    private long _leader;
    private bool _routeExhausted;
    private bool RouteActive => _route is { } route && (route.Travels || route.FleetMode != AutoMiningFleetMode.Solo);
    internal bool IsMonitoring => _flight is not null;

    private void PrepareRoute(PilotCallContext context)
    {
        var route = _route!;
        if (!RouteActive)
        {
            return;
        }
        if (routes is null || systems is null)
        {
            throw new InvalidDataException("AutoMining routes are unavailable.");
        }
        if (route.Systems.Length > 0 && route.FleetMode != AutoMiningFleetMode.Follow && Options.Family != MiningFamily.Ore)
        {
            throw new InvalidDataException("System routes visit asteroid belts. Select Ore, or clear the route for ice and gas.");
        }
        _leader = 0;
        if (route.FleetMode != AutoMiningFleetMode.Solo)
        {
            _leader = (fleets ?? throw new InvalidDataException("AutoMining fleets are unavailable.")).RequireLeader(context.Pilot.PilotId);
            if (route.FleetMode == AutoMiningFleetMode.Lead && _leader != context.Pilot.PilotId)
            {
                throw new InvalidDataException("Only the fleet boss can lead AutoMining.");
            }
        }
        _destination = route.FleetMode == AutoMiningFleetMode.Follow ? fleets!.Follow(context.Pilot.PilotId, _leader) : null;
        if (route.FleetMode != AutoMiningFleetMode.Follow && route.Systems.Any(system => routes.Belts(system).Count == 0))
        {
            throw new InvalidDataException("Every route system must have asteroid belts. Remove systems without belts.");
        }
        _routeStops = new(route.FleetMode == AutoMiningFleetMode.Follow ? [] : route.Systems.SelectMany(routes.Belts));
        if (route.Systems.Length > 0 && route.FleetMode != AutoMiningFleetMode.Follow && _routeStops.Count == 0)
        {
            throw new InvalidDataException("The selected systems have no asteroid belts.");
        }
        int origin = context.Pilot.SystemId;
        foreach (var destination in _routeStops)
        {
            _ = routes.Path(origin, destination.SystemId);
            origin = destination.SystemId;
        }
        if (_destination is { } following)
        {
            _ = routes.Path(context.Pilot.SystemId, following.SystemId);
        }
    }

    private void StartRoute(PilotCallContext context)
    {
        _routeExhausted = false;
        if (!RouteActive)
        {
            return;
        }
        if (_destination is null && _routeStops.TryDequeue(out var first))
        {
            _destination = first;
        }
        if (_destination is { } destination)
        {
            BeginJourney(context, destination, JourneyPurpose.Mining);
        }
        else
        {
            _destination = new(_system, -_ship, context.Pilot.Position!, "Fleet mining position");
        }
        if (_route!.FleetMode == AutoMiningFleetMode.Lead)
        {
            fleets!.Publish(_pilotId, _destination);
        }
    }

    private bool AdvanceRoute(PilotCallContext context)
    {
        try
        {
            if (_route!.FleetMode == AutoMiningFleetMode.Lead)
            {
                fleets!.Publish(_pilotId, _destination!);
            }
            else if (_route.FleetMode == AutoMiningFleetMode.Follow)
            {
                var destination = fleets!.Follow(_pilotId, _leader);
                // Finish the current gate/warp or personal haul before changing course.
                if (_trip is null && _journey is null && destination != _destination)
                {
                    _destination = destination;
                    _routeExhausted = false;
                    BeginJourney(context, destination, JourneyPurpose.Mining);
                }
            }
            if (_journey is not null)
            {
                AdvanceJourney(context);
                return true;
            }
            if (_trip is null && _routeExhausted && _route.FleetMode != AutoMiningFleetMode.Follow && _route.Systems.Length > 0)
            {
                if (mining.HasManualCycle)
                {
                    SetStatus(context, "Waiting for manually started mining before moving on", s_retryInterval);
                }
                else if (_routeStops.TryDequeue(out var next))
                {
                    _destination = next;
                    _routeExhausted = false;
                    BeginJourney(context, next, JourneyPurpose.Mining);
                }
                else
                {
                    Cancel(context, "Route complete: no matching resources remain within reach", settleDue: true);
                }
                return true;
            }
            return false;
        }
        catch (InvalidDataException error)
        {
            Cancel(context, "Paused: " + error.Message, settleDue: true);
            return true;
        }
    }

    private void BeginJourney(PilotCallContext context, AutoMiningDestination destination, JourneyPurpose purpose)
    {
        if (mining.HasManualCycle)
        {
            throw new InvalidDataException("Stop manually started harvesting before travelling with AutoMining.");
        }
        var path = routes!.Path(context.Pilot.SystemId, destination.SystemId);
        StopApproach(context);
        mining.StopAutomatic(context, settleDue: true);
        targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
        _journey = new(destination, purpose, new(path), clock.GetTimestamp());
        _miningOrigin = null;
        UpdateStatus(context, "Travelling to " + destination.Name);
    }

    private void AdvanceJourney(PilotCallContext context)
    {
        var journey = _journey!;
        var flight = _flight!;
        if (clock.GetElapsedTime(journey.Started) >= s_travelTimeout)
        {
            throw new InvalidDataException("The mining route timed out waiting for travel or the flight scene.");
        }
        if (_expectedSystem is not null || !flight.InBallpark || flight.InWarp || flight.PreparingWarp)
        {
            return;
        }
        if (context.Pilot.Position is not { } position)
        {
            throw new InvalidDataException("The ship left space during the mining route.");
        }
        while (journey.Gates.TryPeek(out var passed) && passed.SystemId != _system)
        {
            journey.Gates.Dequeue();
        }
        if (journey.Gates.TryPeek(out var leg))
        {
            var gate = flight.LocalObject(leg.Id) ?? throw new InvalidDataException("The route stargate is unavailable.");
            if (GateTravelRules.InRange(position, context.Pilot.Flight.ShipRadius, gate))
            {
                var result = flight.StartJump(new(leg.Id, leg.ExitId, _ship), systems!, automatic: true);
                if (result is not PilotCallResult.Success)
                {
                    throw new InvalidDataException(
                        result is PilotCallResult.Failure error ? error.Message : "The route gate rejected the jump."
                    );
                }
                _expectedSystem = leg.DestinationSystemId;
                UpdateStatus(context, "Jumping to the next route system");
            }
            else
            {
                MoveOnJourney(context, gate.Id, gate.Position, gate.Radius, point: false);
            }
            return;
        }
        var destination = journey.Destination;
        if (_system != destination.SystemId)
        {
            throw new InvalidDataException("The mining route arrived in an unexpected system.");
        }
        double radius = journey.Purpose == JourneyPurpose.Unload ? flight.LocalStation(destination.Id)?.Radius ?? 0 : 0;
        double tolerance = ReturnTolerance + radius + (journey.Purpose == JourneyPurpose.Unload ? context.Pilot.Flight.ShipRadius : 0);
        if (position.DistanceTo(destination.Position) > tolerance)
        {
            MoveOnJourney(context, destination.Id, destination.Position, radius, point: journey.Purpose != JourneyPurpose.Unload);
            return;
        }
        ExecuteAutomaticCommand(flight, new FlightCommand.Stop(), "Could not stop at the mining destination.");
        _journey = null;
        _nextDecision = clock.GetTimestamp();
        if (journey.Purpose == JourneyPurpose.Unload)
        {
            EnterPhase(context, HaulPhase.Departing, "Arrived in the unload system");
        }
        else if (journey.Purpose == JourneyPurpose.Return)
        {
            ResumeMining(context, _trip!);
        }
        else
        {
            _miningOrigin = position;
            UpdateStatus(context, "Arrived at " + destination.Name);
        }
    }

    private void MoveOnJourney(PilotCallContext context, long id, SpacePosition destination, double radius, bool point)
    {
        var flight = _flight!;
        double distance = context.Pilot.Position!.DistanceTo(destination) - radius - context.Pilot.Flight.ShipRadius;
        if (distance >= NavigationDefinitions.MinimumWarpDistance)
        {
            if (_journey!.MovingTo == id)
            {
                throw new InvalidDataException("The warp did not reach the route destination.");
            }
            ExecuteAutomaticCommand(
                flight,
                point ? new FlightCommand.WarpToPoint(id, destination, 0) : new FlightCommand.WarpTo(id, 0),
                "Could not warp along the mining route."
            );
            _journey = _journey with { MovingTo = id };
        }
        else if (point)
        {
            var heading = (destination - context.Pilot.Position!).Unit;
            ExecuteAutomaticCommand(
                flight,
                new FlightCommand.Heading(heading.X, heading.Y, heading.Z),
                "Could not approach the mining site."
            );
        }
        else
        {
            ExecuteAutomaticCommand(flight, new FlightCommand.Follow(id, 0, false), "Could not approach the route destination.");
        }
    }
}
