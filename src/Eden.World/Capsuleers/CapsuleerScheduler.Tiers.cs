using Eden.Gameplay.Capsuleers;
using Eden.World.Fittings;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private const int BackgroundStartsPerInterval = 4;
    private readonly SortedSet<(long DueTicks, long Pilot)> _waitingBackground = [];
    private long _nextBackgroundStartTicks;

    private bool WaitingForBackgroundStart(long pilot) => _waitingBackground.Contains((Current[pilot].DueTicks, pilot));

    private bool RemoveBackgroundStart(long pilot) => _waitingBackground.Remove((Current[pilot].DueTicks, pilot));

    private long? NextWorkTicks() =>
        _waitingBackground.Count == 0 ? _queue.NextDueTicks : Math.Min(_queue.NextDueTicks ?? long.MaxValue, _nextBackgroundStartTicks);

    private int StartBackgroundPilots(long elapsedTicks)
    {
        // Let already eligible jobs drain before adding more work. An overloaded turn
        // or a period with no humans must never accumulate a credit for future starts.
        if (_waitingBackground.Count == 0 || elapsedTicks < _nextBackgroundStartTicks || _queue.NextDueTicks <= elapsedTicks)
        {
            return 0;
        }
        _nextBackgroundStartTicks = checked(elapsedTicks + CapsuleerJobQueue.WakeInterval.Ticks);
        int count = 0;
        for (; count < BackgroundStartsPerInterval && _waitingBackground.Count > 0; count++)
        {
            var start = _waitingBackground.Min;
            StartPilot(start.Pilot, elapsedTicks);
            _waitingBackground.Remove(start);
        }
        return count;
    }

    private void StartPilot(long pilot, long elapsedTicks)
    {
        bool firstEntry = _newPilots.Remove(pilot);
        if (_paused.TryGetValue(pilot, out var pause))
        {
            if (pause.ElapsedTicks < elapsedTicks || pause.Timestamp != clock.GetTimestamp())
            {
                _resuming[pilot] = elapsedTicks;
            }
            else
            {
                _paused.Remove(pilot);
            }
        }
        else if (firstEntry && elapsedTicks > 0)
        {
            _resuming[pilot] = elapsedTicks;
        }
        _queue.Publish(ScheduledJob(pilot));
    }

    private static bool SupportsBackground(CapsuleerCareer career) => career is CapsuleerCareer.Mining or CapsuleerCareer.Hauling;

    private bool CanRunUnobserved(long pilot) => SupportsBackground(_registrations.GetValueOrDefault(pilot).Career);

    private CapsuleerMiner? MinerWorker(long pilot)
    {
        if (_registrations.GetValueOrDefault(pilot).Career != CapsuleerCareer.Mining)
        {
            return null;
        }
        if (!_miners.TryGetValue(pilot, out var worker))
        {
            worker = miners.Create(pilot, _miningCrews[_registrations[pilot].CrewSystem], id => _suspended.Contains(id));
            _miners.Add(pilot, worker);
        }
        return worker;
    }

    private void RebaseMiningCrew(long pilot, CapsuleerMiner worker, int system)
    {
        var registration = _registrations[pilot];
        if (system == registration.CrewSystem)
        {
            return;
        }
        _miningCrews[registration.CrewSystem].Remove(pilot);
        if (!_miningCrews.TryGetValue(system, out var crew))
        {
            crew = [];
            _miningCrews.Add(system, crew);
        }
        crew.Add(pilot);
        _registrations[pilot] = registration with { CrewSystem = system };
        worker.ChangeCrew(crew);
    }

    private CapsuleerHauler? HaulerWorker(long pilot)
    {
        if (_registrations.GetValueOrDefault(pilot).Career != CapsuleerCareer.Hauling)
        {
            return null;
        }
        if (!_haulers.TryGetValue(pilot, out var worker))
        {
            worker = haulers.Create(pilot);
            _haulers.Add(pilot, worker);
        }
        return worker;
    }

    private void ReleaseWorker(long pilot)
    {
        _miners.Remove(pilot);
        _haulers.Remove(pilot);
    }

    private void WakeObservedMiners(long elapsed, IReadOnlyList<SystemSimulationState> transitions)
    {
        if (transitions.Count == 0 || Live is not { } live)
        {
            return;
        }
        long pulse = checked(elapsed + TimeSpan.TicksPerSecond);
        foreach (var state in transitions.Where(state => state.Mode == SystemSimulationMode.Activating))
        {
            foreach (long pilot in _systemAgents.GetValueOrDefault(state.SystemId) ?? [])
            {
                if (
                    _running.Contains(pilot)
                    && live.MinerCheckpoints.GetValueOrDefault(pilot)?.Work is { Activity: MinerActivity.Mining } work
                    && Current[pilot].DueTicks == work.DueTicks
                    && work.DueTicks > pulse
                )
                {
                    // Publish a threat-check deadline while retaining the paid collection deadline in Work.
                    WakePilot(pilot, pulse);
                }
            }
        }
    }

    private void SettleReadySystems(HumanActivitySnapshot activity)
    {
        foreach (var state in _systems.Observed())
        {
            long due = ReadyDeadline(state.SystemId);
            if (due > activity.ElapsedTicks)
            {
                _systems.Settle(state.SystemId, Math.Max(state.SettledTicks, activity.ElapsedTicks));
            }
        }
    }

    private sealed record SystemReadiness(Eden.World.Runtime.LivePresentationVersion Version, long Membership, long DueTicks);

    private readonly Dictionary<int, SystemReadiness> _readiness = [];

    private long ReadyDeadline(int system)
    {
        var version = Live?.PresentationVersion(system);
        if (
            version is { } current
            && _readiness.TryGetValue(system, out var previous)
            && previous.Version == current
            && previous.Membership == Reconciliations
        )
        {
            return previous.DueTicks;
        }
        long due = long.MaxValue;
        foreach (long pilot in _systemAgents.GetValueOrDefault(system) ?? [])
        {
            if (_running.Contains(pilot))
            {
                due = Math.Min(due, ScheduledJob(pilot).DueTicks);
            }
        }
        if (version is { } captured)
        {
            _readiness[system] = new(captured, Reconciliations, due);
        }
        return due;
    }

    private void UpdateDetailRetention(HumanActivitySnapshot activity, IReadOnlyList<SystemSimulationState> transitions)
    {
        var observed = activity.ActiveSystems.SelectMany(system => _systemAgents.GetValueOrDefault(system) ?? []);
        ResidentFitCache.Find(database.Path)?.Pin(activity.Pilots.Select(p => p.PilotId).Concat(observed.Where(_running.Contains)));
        if (Live is not { } live)
        {
            return;
        }
        live.PublishDetailedSystems(activity.ActiveSystems);
        foreach (var state in transitions)
        {
            if (state.Mode == SystemSimulationMode.Deactivating)
            {
                live.ReleaseSystemDetails(state.SystemId);
            }
        }
    }
}
