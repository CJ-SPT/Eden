using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Fittings;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private const int BackgroundStartsPerInterval = 4;
    private readonly SortedSet<(long DueTicks, long Pilot)> _waitingBackground = [];
    private readonly Dictionary<long, long> _waitingDeadlines = [];
    private long _nextBackgroundStartTicks;

    private void QueueBackgroundStart(long pilot, long dueTicks)
    {
        if (_waitingDeadlines.TryAdd(pilot, dueTicks))
        {
            _waitingBackground.Add((dueTicks, pilot));
        }
    }

    private void RemoveBackgroundStart(long pilot)
    {
        if (_waitingDeadlines.Remove(pilot, out long dueTicks))
        {
            _waitingBackground.Remove((dueTicks, pilot));
        }
    }

    private void ClearBackgroundStarts()
    {
        _waitingBackground.Clear();
        _waitingDeadlines.Clear();
    }

    private long? NextWorkTicks() =>
        _waitingBackground.Count == 0 ? _queue.NextDueTicks : Math.Min(_queue.NextDueTicks ?? long.MaxValue, _nextBackgroundStartTicks);

    private int StartBackgroundPilots(long elapsedTicks)
    {
        if (_waitingBackground.Count == 0 || elapsedTicks < _nextBackgroundStartTicks)
        {
            return 0;
        }
        int limit = BackgroundStartsPerInterval;
        if (_queue.NextDueTicks is long oldest && oldest <= elapsedTicks)
        {
            // An expanded fresh roster can keep some work due even while healthy.
            // Let one current-time start join the existing deadlines per interval.
            var settings = _populationSettings.Settings;
            if (
                !settings.FreshUniverse
                || settings.Capsuleers.FreshFounderCohortsPerSystem == FreshCareerRules.DefaultFounderCohorts
                || elapsedTicks - oldest >= CapsuleerJobQueue.WakeInterval.Ticks * 2
            )
            {
                return 0;
            }
            limit = 1;
        }
        _nextBackgroundStartTicks = checked(elapsedTicks + CapsuleerJobQueue.WakeInterval.Ticks);
        int count = 0;
        for (; count < limit && _waitingBackground.Count > 0; count++)
        {
            var start = _waitingBackground.Min;
            if (!_registrations.ContainsKey(start.Pilot))
            {
                WorldActionScope.Inspect(database, world => Register(world, [world.Set<SavedCapsuleerAgent>().Find(start.Pilot)!]));
            }
            StartPilot(start.Pilot, elapsedTicks);
            _running.Add(start.Pilot);
            RemoveBackgroundStart(start.Pilot);
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

    private HashSet<long> JoinMiningCrew(long pilot, int system)
    {
        if (!_miningCrews.TryGetValue(system, out var crew))
        {
            crew = [];
            _miningCrews.Add(system, crew);
        }
        crew.Add(pilot);
        return crew;
    }

    private static void RestoreMiningCrew(WorldAction world, LiveWorld live, HashSet<long> crew)
    {
        foreach (long member in crew)
        {
            if (!live.MinerCheckpoints.ContainsKey(member) && !live.ProjectedMiners.ContainsKey(member))
            {
                var row = world.Filter<SavedCapsuleerCommunity>(value => value.PilotId == member).SingleOrDefault();
                if (MinerCheckpoint.Read(row?.Progress) is { } progress)
                {
                    live.ProjectedMiners.Add(member, new(member, progress));
                }
            }
        }
    }

    private void RebaseMiningCrew(long pilot, CapsuleerMiner worker, int system)
    {
        var registration = _registrations[pilot];
        if (system == registration.CrewSystem)
        {
            return;
        }
        RemoveMember(_miningCrews, registration.CrewSystem, pilot);
        _registrations[pilot] = registration with { CrewSystem = system };
        worker.ChangeCrew(JoinMiningCrew(pilot, system));
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
        _offscreenPresence.Remove(pilot);
        _miners.Remove(pilot);
        _haulers.Remove(pilot);
    }

    private bool ApplyWorkerChange(LiveWorld? live, long pilot, int system, bool restoreCrew)
    {
        if (live is null || !live.CapsuleerWorkerChanges.Remove(pilot, out var career))
        {
            return false;
        }

        // The current action has committed its replacement checkpoint and canonical job.
        // Drop both retained observations before loading the incoming worker.
        ReleaseWorker(pilot);
        _pendingMiners.Remove(pilot);
        _pendingHaulers.Remove(pilot);
        _presentations.Remove(system);
        _readiness.Remove(system);
        _diagnosticTimestamp = null;

        var registration = _registrations[pilot];
        if (registration.Career != career)
        {
            if (registration.Career == CapsuleerCareer.Mining)
            {
                RemoveMember(_miningCrews, registration.CrewSystem, pilot);
            }
            _registrations[pilot] = registration with { Career = career, CrewSystem = system };
            if (career == CapsuleerCareer.Mining)
            {
                var crew = JoinMiningCrew(pilot, system);
                if (restoreCrew && crew.Count > 1)
                {
                    WorldActionScope.Inspect(database, world => RestoreMiningCrew(world, live, crew));
                }
            }
        }

        return true;
    }

    private CapsuleerCareer? DormantCareer(long pilot)
    {
        if (_suspended.Contains(pilot))
        {
            return null;
        }
        if (_registrations.TryGetValue(pilot, out var registration))
        {
            return registration.Career;
        }

        var resident = Live!.Economy;
        var career = resident.Peek<SavedCapsuleerAgent>(pilot)?.Career;
        return career is null || resident.Peek<SavedCapsuleerOrganization>(pilot) is null
            ? career
            : WorldActionScope.Inspect(database, world => CapsuleerCorporations.Organization(world, pilot).ActiveWorker) ?? career;
    }

    private CapsuleerMiner? ReadDormantMiner(long pilot) =>
        DormantCareer(pilot) == CapsuleerCareer.Mining ? miners.Create(pilot, [], id => _suspended.Contains(id)) : null;

    private CapsuleerHauler? ReadDormantHauler(long pilot) =>
        DormantCareer(pilot) == CapsuleerCareer.Hauling ? haulers.Create(pilot) : null;

    private void ReleaseRegistration(LiveWorld live, long pilot)
    {
        ReleaseWorker(pilot);
        var registration = _registrations[pilot];
        _registrations.Remove(pilot);
        _newPilots.Remove(pilot);
        int system = _locations[pilot];
        _locations.Remove(pilot);
        RemoveMember(_systemAgents, system, pilot);
        RemoveMember(_miningCrews, registration.CrewSystem, pilot);
        if (_pilotShips.Remove(pilot, out long ship))
        {
            _ships.Remove(ship);
        }
        live.ReleaseCapsuleerDetails(pilot, system);
    }

    private static void RemoveMember(Dictionary<int, HashSet<long>> groups, int group, long pilot)
    {
        if (groups.TryGetValue(group, out var members) && members.Remove(pilot) && members.Count == 0)
        {
            groups.Remove(group);
        }
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
                    && !_resuming.ContainsKey(pilot)
                    && live.MinerCheckpoints.GetValueOrDefault(pilot)?.Work.Navigation.Warp is not null
                    && Current[pilot].DueTicks > elapsed
                    && _miners.TryGetValue(pilot, out var traveler)
                )
                {
                    traveler.Sample(elapsed);
                    WakePilot(pilot, Math.Min(pulse, Current[pilot].DueTicks));
                }
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
