using Eden.Gameplay.Flight;
using Eden.Gameplay.Mining;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Mining;

internal sealed partial class AutoMiningHandler
{
    private const double ApproachRangeFraction = 0.8;
    private long? _approachTarget;
    private long _jettisonReadyAt;

    private void RestartSelection(PilotCallContext context)
    {
        _routeExhausted = false;
        if (_flight is null || _trip is not null || _journey is not null)
        {
            return;
        }
        context.Flight.Checkpoint();
        CheckFlight(context);
        if (_flight is null)
        {
            return;
        }
        StopApproach(context);
        targeting.Advance(context);
        mining.StopAutomatic(context, settleDue: true);
        targeting.ReleaseAutoMiningLocks(context, mining.IsMiningTarget);
        _nextDecision = clock.GetTimestamp();
    }

    private AutoMiningCandidate[] RankedTargets(PilotCallContext context, AutoMiningModule[] idle, double lockRange)
    {
        var candidates = AutoMiningCandidate.Nearest(
            _flight!.VisibleAsteroids.Where(rock =>
                Options.Matches(rock, _filter!) && !targeting.IsExcludedFromAutoMining(rock.Id) && idle.Any(module => module.Matches(rock))
            ),
            context.Pilot.Position!,
            context.Pilot.Flight.ShipRadius,
            Options.Approach ? double.PositiveInfinity : Math.Min(lockRange, idle.Max(module => module.Range))
        );
        if (Options.TargetOrder == AutoMiningTargetOrder.Nearest)
        {
            return candidates;
        }
        var volume = store.Survey(_pilotId, _system, candidates.Select(candidate => candidate.Rock.Id).ToArray());
        var surveyed = candidates.Where(candidate => volume.ContainsKey(candidate.Rock.Id));
        return Options.TargetOrder == AutoMiningTargetOrder.Largest
            ? surveyed.OrderByDescending(candidate => volume[candidate.Rock.Id]).ToArray()
            : surveyed.OrderBy(candidate => volume[candidate.Rock.Id]).ToArray();
    }

    private bool TryApproach(
        PilotCallContext context,
        AutoMiningCandidate[] ranked,
        AutoMiningCandidate[] reachable,
        AutoMiningModule[] idle,
        double lockRange
    )
    {
        if (
            !Options.Approach
            || mining.HasCycle
            || ranked.Length == 0
            || lockRange <= 0
            || reachable.Any(candidate => targeting.IsPending(candidate.Rock.Id))
        )
        {
            return false;
        }
        // Keep a valid follow order through a survey refresh, so small changes in quantity cannot steer the ship back and forth.
        var rock = ranked.FirstOrDefault(candidate => candidate.Rock.Id == _approachTarget).Rock ?? ranked[0].Rock;
        if (reachable.Any(candidate => candidate.Rock.Id == rock.Id))
        {
            return false;
        }
        if (_approachTarget != rock.Id)
        {
            double range = Math.Min(lockRange, idle.Where(module => module.Matches(rock)).Max(module => module.Range));
            ExecuteAutomaticCommand(
                _flight!,
                new FlightCommand.Follow(rock.Id, Math.Min(range * ApproachRangeFraction, NavigationDefinitions.MaximumFollowRange), false),
                "Could not approach the mining target."
            );
            _approachTarget = rock.Id;
        }
        SetStatus(context, Options.Family == MiningFamily.Gas ? "Approaching gas cloud" : "Approaching asteroid", s_retryInterval);
        return true;
    }

    private void StopApproach(PilotCallContext context)
    {
        if (_approachTarget is null)
        {
            return;
        }
        _approachTarget = null;
        if (
            _flight is { } flight
            && ReferenceEquals(context.ActiveFlight, flight)
            && CanRun(flight)
            && _navigationRevision == flight.PilotNavigationRevision
        )
        {
            _ = flight.Execute(new FlightCommand.Stop(), automatic: true);
        }
    }

    private bool TryJettison(PilotCallContext context)
    {
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (now < _jettisonReadyAt)
        {
            SetStatus(context, "Waiting for the jettison cooldown", s_retryInterval);
            return true;
        }
        var result = store.Jettison(_pilotId, _ship, Options.Family);
        if (result.RetryAfter > 0)
        {
            _jettisonReadyAt = checked(now + result.RetryAfter);
            SetStatus(context, "Waiting for the jettison cooldown", s_retryInterval);
            return true;
        }
        if (result.Contact is not { } contact)
        {
            SetStatus(context, "No harvested units fit in a jetcan", s_blockedInterval);
            return true;
        }
        foreach (var change in result.Changes)
        {
            InventoryChangesWire.NotifyItemChange(context, change);
        }
        context.Flight.AddWreck(contact);
        SetStatus(context, "Harvest jettisoned", s_retryInterval);
        return true;
    }
}
