using Eden.Gameplay.Mining;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Mining;

internal sealed partial class AutoMiningHandler
{
    private readonly List<AutoMiningDestination> _unsafeSites = [];
    private bool _retreating;
    private bool _evading;

    private AutoMiningDestination? NextSafeStop()
    {
        while (_routeStops.TryDequeue(out var next))
        {
            if (!_unsafeSites.Any(site => AutoMiningSafetyRules.SameGrid(site, next)))
            {
                return next;
            }
        }
        return null;
    }

    private bool AvoidRats(PilotCallContext context, bool sessionTick)
    {
        var flight = _flight!;
        if (
            _retreating
            || _evading
            || routes is null
            || systems is null
            || !flight.InBallpark
            || flight.InWarp
            || flight.PreparingWarp
            || _expectedSystem is not null
            || context.Pilot.Position is not { } position
            || !flight.VisibleNpcs.Any(npc => AutoMiningSafetyRules.Threat(npc, _ship, _system, position))
        )
        {
            return false;
        }
        if (!sessionTick)
        {
            return true;
        }
        try
        {
            if (mining.HasManualCycle)
            {
                Cancel(context, "Rats detected: stop manual harvesting and warp away.", settleDue: true);
                return true;
            }
            var unsafeSite = new AutoMiningDestination(_system, 0, position, "Rats detected");
            if (!_unsafeSites.Any(site => AutoMiningSafetyRules.SameGrid(site, unsafeSite)))
            {
                _unsafeSites.Add(unsafeSite);
            }
            var next =
                _journey is { Purpose: JourneyPurpose.Mining } pending
                && !_unsafeSites.Any(site => AutoMiningSafetyRules.SameGrid(site, pending.Destination))
                    ? pending.Destination
                    : NextSafeStop();
            if (_trip is null && _route?.FleetMode != AutoMiningFleetMode.Follow && next is not null)
            {
                _destination = next;
                _routeExhausted = false;
                BeginJourney(context, next, JourneyPurpose.Mining);
                _evading = true;
                UpdateStatus(context, "Avoiding rats: moving to " + next.Name);
            }
            else
            {
                var refuge =
                    routes.Refuge(_system, position)
                    ?? throw new InvalidDataException(
                        "Rats detected, but no station or stargate is available for retreat. Warp away manually."
                    );
                _trip = null;
                BeginJourney(context, refuge, JourneyPurpose.Refuge);
                _retreating = true;
                fleets?.Stop(_pilotId);
                UpdateStatus(context, "Avoiding rats: retreating to " + refuge.Name);
            }
            AdvanceJourney(context);
        }
        catch (InvalidDataException error)
        {
            Cancel(context, "Unable to avoid rats: " + error.Message, settleDue: true);
        }
        return true;
    }
}
