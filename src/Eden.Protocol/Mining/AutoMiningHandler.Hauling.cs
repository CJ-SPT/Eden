using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Mining;

internal sealed partial class AutoMiningHandler
{
    private enum HaulPhase
    {
        Departing,
        ToStation,
        ApproachingStation,
        Unloading,
        Undocking,
        WaitingForBallpark,
        Returning,
        ApproachingOrigin,
    }

    private sealed record HaulTrip(long StationId, SpacePosition Origin, int OriginSystem, HaulPhase Phase, long Started);

    // Account for the standard point-warp offset and the last braking tick.
    private const double ReturnTolerance = 2500;
    private static readonly TimeSpan s_travelTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan s_ballparkTimeout = TimeSpan.FromSeconds(60);

    // Build 3662168 uses a ten-second session transition window after docking.
    private static readonly TimeSpan s_stationWait = TimeSpan.FromSeconds(10);
    private HaulTrip? _trip;
    private AutoMiningHaulSettings? _haulSettings;
    private long _navigationRevision;
    private SpacePosition? _miningOrigin;

    internal bool IsHauling => _trip is not null;

    private bool UnloadEnabled => Options.Jettison || _haulSettings is { Enabled: true };

    private void UpdateRepeatThreshold() =>
        mining.AutoRepeatThreshold = _flight is not null && UnloadEnabled ? _haulSettings!.Threshold : null;

    private bool TryBeginHaul(PilotCallContext context, bool unitDoesNotFit = false)
    {
        if (!UnloadEnabled || _haulSettings is not { } settings || hauling is null || _flight is not { } flight)
        {
            return false;
        }
        var hold = hauling.ReadHold(_pilotId, _ship, Options.Family);
        if (!hold.ShouldUnload(settings.Threshold, unitDoesNotFit))
        {
            return false;
        }
        if (mining.HasManualCycle)
        {
            SetStatus(context, "Waiting for manually started mining to stop before unloading", s_retryInterval);
            return true;
        }
        StopApproach(context);
        if (Options.Jettison)
        {
            return TryJettison(context);
        }
        var station = flight.LocalStation(settings.StationId);
        if (station is null && !RouteActive)
        {
            Cancel(context, "Paused: choose an unload station in this system.", settleDue: true);
            return true;
        }

        mining.StopAutomatic(context, settleDue: true);
        targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
        _miningOrigin ??= context.Pilot.Position!;
        _trip = new(settings.StationId, _miningOrigin, _system, HaulPhase.Departing, clock.GetTimestamp());
        if (station is null)
        {
            try
            {
                BeginJourney(context, routes!.Station(settings.StationId), JourneyPurpose.Unload);
            }
            catch (InvalidDataException error)
            {
                Cancel(context, "Paused: " + error.Message);
            }
            return true;
        }
        UpdateStatus(context, "Heading to unload station");
        AdvanceHaul(context);
        return true;
    }

    private void AdvanceHaul(PilotCallContext context)
    {
        var trip = _trip!;
        var flight = _flight!;
        try
        {
            switch (trip.Phase)
            {
                case HaulPhase.Departing or HaulPhase.ToStation or HaulPhase.ApproachingStation:
                    TravelToStation(context, trip);
                    break;
                case HaulPhase.Unloading:
                    foreach (var change in hauling!.Unload(_pilotId, _ship, trip.StationId, Options.Family))
                    {
                        InventoryChangesWire.NotifyItemChange(context, change);
                    }
                    EnterPhase(
                        context,
                        HaulPhase.Undocking,
                        Options.Family == MiningFamily.Ore ? "Ore unloaded. Preparing to undock" : "Hold unloaded. Preparing to undock"
                    );
                    break;
                case HaulPhase.Undocking:
                    if (clock.GetElapsedTime(trip.Started) >= s_stationWait)
                    {
                        ExecuteAutomaticCommand(flight, new FlightCommand.Undock(_ship), "Could not undock from the unload station.");
                        EnterPhase(context, HaulPhase.WaitingForBallpark, "Waiting for the flight scene");
                    }
                    break;
                case HaulPhase.WaitingForBallpark:
                    if (!flight.InBallpark)
                    {
                        if (clock.GetElapsedTime(trip.Started) >= s_ballparkTimeout)
                        {
                            throw new InvalidDataException("The flight scene did not become ready after undocking.");
                        }
                        break;
                    }
                    BeginReturn(context, trip);
                    break;
                case HaulPhase.Returning:
                    if (!flight.InWarp && !flight.PreparingWarp)
                    {
                        double distance = flight.Pilot.Position!.DistanceTo(trip.Origin);
                        if (distance <= ReturnTolerance)
                        {
                            ResumeMining(context, trip);
                        }
                        else if (distance <= ReturnTolerance + flight.Pilot.Flight.ShipRadius)
                        {
                            ApproachOrigin(context, trip);
                        }
                        else
                        {
                            throw new InvalidDataException("The ship could not return to the saved mining position.");
                        }
                    }
                    else
                    {
                        CheckTravelTimeout(trip);
                    }
                    break;
                case HaulPhase.ApproachingOrigin:
                    if (flight.Pilot.Position!.DistanceTo(trip.Origin) <= ReturnTolerance)
                    {
                        ExecuteAutomaticCommand(flight, new FlightCommand.Stop(), "Could not stop at the mining site.");
                        ResumeMining(context, trip);
                    }
                    else
                    {
                        CheckTravelTimeout(trip);
                    }
                    break;
            }
        }
        catch (InvalidDataException error)
        {
            Cancel(context, "Paused: " + error.Message);
        }
        catch (CargoCapacityException)
        {
            Cancel(context, "Paused: the resources could not be transferred to the personal hangar.");
        }
    }

    private void TravelToStation(PilotCallContext context, HaulTrip trip)
    {
        var flight = _flight!;
        var station = flight.LocalStation(trip.StationId) ?? throw new InvalidDataException("The unload station is unavailable.");
        if (flight.InWarp || flight.PreparingWarp)
        {
            CheckTravelTimeout(trip);
            return;
        }
        if (flight.NearEnoughToDock(station))
        {
            ExecuteAutomaticCommand(flight, new FlightCommand.Dock(station.Id, _ship), "Could not dock at the unload station.");
            EnterPhase(context, HaulPhase.Unloading, "Docked. Unloading the mining hold");
            return;
        }
        var position = flight.Pilot.Position ?? throw new InvalidDataException("The ship left space before reaching the unload station.");
        if (trip.Phase == HaulPhase.ApproachingStation)
        {
            CheckTravelTimeout(trip);
            return;
        }
        double distance = position.DistanceTo(CelestialWarpPoint.Resolve(station, position)) - flight.Pilot.Flight.ShipRadius;
        if (distance >= NavigationDefinitions.MinimumWarpDistance)
        {
            if (trip.Phase == HaulPhase.ToStation)
            {
                throw new InvalidDataException("The warp did not reach the unload station.");
            }
            ExecuteAutomaticCommand(flight, new FlightCommand.WarpTo(station.Id, 0), "Could not warp to the unload station.");
            EnterPhase(context, HaulPhase.ToStation, "Warping to the unload station");
        }
        else
        {
            ExecuteAutomaticCommand(flight, new FlightCommand.Follow(station.Id, 0, false), "Could not approach the unload station.");
            EnterPhase(context, HaulPhase.ApproachingStation, "Approaching the unload station");
        }
    }

    private void BeginReturn(PilotCallContext context, HaulTrip trip)
    {
        if (_route?.FleetMode == AutoMiningFleetMode.Follow)
        {
            var destination = fleets!.Follow(_pilotId, _leader);
            _destination = destination;
            _trip = null;
            BeginJourney(context, destination, JourneyPurpose.Mining);
            return;
        }
        if (_system != trip.OriginSystem)
        {
            BeginJourney(context, new(trip.OriginSystem, -_ship, trip.Origin, "Saved mining position"), JourneyPurpose.Return);
            return;
        }
        var flight = _flight!;
        var position = flight.Pilot.Position!;
        double distance = position.DistanceTo(trip.Origin);
        if (distance <= ReturnTolerance)
        {
            ExecuteAutomaticCommand(flight, new FlightCommand.Stop(), "Could not stop at the mining site.");
            ResumeMining(context, trip);
        }
        else if (distance - flight.Pilot.Flight.ShipRadius < NavigationDefinitions.MinimumWarpDistance)
        {
            ApproachOrigin(context, trip);
        }
        else
        {
            // Reuse bookmark-style point warp and the normal landing offset.
            ExecuteAutomaticCommand(
                flight,
                new FlightCommand.WarpToPoint(-_ship, trip.Origin, 0),
                "Could not warp back to the mining site."
            );
            EnterPhase(context, HaulPhase.Returning, "Returning to the mining site");
        }
    }

    private void ApproachOrigin(PilotCallContext context, HaulTrip trip)
    {
        var flight = _flight!;
        var direction = (trip.Origin - flight.Pilot.Position!).Unit;
        ExecuteAutomaticCommand(
            flight,
            new FlightCommand.Heading(direction.X, direction.Y, direction.Z),
            "Could not approach the mining site."
        );
        EnterPhase(context, HaulPhase.ApproachingOrigin, "Approaching the saved mining position");
    }

    private void ResumeMining(PilotCallContext context, HaulTrip trip)
    {
        var flight = _flight!;
        if (!CanRun(flight) || flight.Pilot.Position!.DistanceTo(trip.Origin) > ReturnTolerance)
        {
            throw new InvalidDataException("The ship could not return to the saved mining position.");
        }
        var modules = store.Modules(_pilotId, _ship);
        if (
            !RouteActive
            && !flight.VisibleAsteroids.Any(rock =>
                Options.Matches(rock, _filter!)
                && !targeting.IsExcludedFromAutoMining(rock.Id)
                && modules.Any(module =>
                    module.Family == Options.Family
                    && module.Matches(rock)
                    && (
                        Options.Approach
                        || flight.Pilot.Position!.DistanceTo(rock.Position) - flight.Pilot.Flight.ShipRadius - rock.Radius <= module.Range
                    )
                )
            )
        )
        {
            throw new InvalidDataException("There is no matching resource within reach of the saved position.");
        }
        _trip = null;
        SetStatus(context, "Back at the mining site", s_retryInterval);
    }

    private void EnterPhase(PilotCallContext context, HaulPhase phase, string status)
    {
        _trip = _trip! with { Phase = phase, Started = clock.GetTimestamp() };
        UpdateStatus(context, status);
    }

    private void CheckTravelTimeout(HaulTrip trip)
    {
        if (clock.GetElapsedTime(trip.Started) >= s_travelTimeout)
        {
            throw new InvalidDataException("The haul trip timed out in flight.");
        }
    }

    private static void ExecuteAutomaticCommand(FlightSession flight, FlightCommand command, string message)
    {
        var result = flight.Execute(command, automatic: true);
        if (result is not PilotCallResult.Success)
        {
            throw new InvalidDataException(result is PilotCallResult.Failure error ? error.Message : message);
        }
    }
}
