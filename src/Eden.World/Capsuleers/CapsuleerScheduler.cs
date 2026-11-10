using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed partial class CapsuleerScheduler(
    WorldDatabase database,
    CapsuleerActivitySource presence,
    ICapsuleerPopulation population,
    IWorldSynchronization synchronization,
    TimeProvider clock,
    CapsuleerMinerFactory miners,
    CapsuleerHaulerFactory haulers,
    ICapsuleerGateWatch gateWatch,
    MarketChangeSignal marketSignal
) : ICapsuleerScheduler, ICapsuleerSimulation, ICapsuleerHauling, ICapsuleerHandoff, ICapsuleerDiagnostics, ICapsuleerUpdateDiagnostics
{
    private readonly SystemSimulationRegistry _systems = new();
    private readonly Dictionary<long, MinerObservation> _pendingMiners = [];
    private readonly Dictionary<long, HaulerObservation> _pendingHaulers = [];
    private readonly HashSet<int> _activated = [];
    private HashSet<long> _running = [];
    private HashSet<long> _nextRunning = [];
    private readonly HashSet<long> _turnPilots = [];
    private readonly HashSet<int> _visited = [];
    private readonly HashSet<long> _legacyTravelers = [];
    private long _presentationRevision;
    private CapsuleerBatch? _lastPresentation;
    private long _settledTicks;
    private int _destinyStamp;
    private CapsuleerRuntimeSummary _diagnostics = CapsuleerRuntimeSummary.Empty;
    private CapsuleerPopulationSummary _population = new(0, 0, 0, 0, 0);
    private readonly Dictionary<long, string> _blocked = [];
    private IReadOnlyList<CapsuleerBlockingReason> _blockingReasons = [];
    private bool _blockingChanged;
    private long _completed;
    private long _failures;
    private string? _lastFailure;

    public CapsuleerRuntimeSummary Read() => Volatile.Read(ref _diagnostics);

    public void RecordUpdate(CapsuleerUpdateTiming timing)
    {
        lock (synchronization.Gate)
        {
            Volatile.Write(ref _diagnostics, _diagnostics with { LastUpdate = timing.Total, LastPresentation = timing.Presentation });
        }
    }

    private void Blocking(long pilot, string? reason)
    {
        if (_blocked.GetValueOrDefault(pilot) == reason)
        {
            return;
        }
        if (reason is null)
        {
            _blocked.Remove(pilot);
        }
        else
        {
            _blocked[pilot] = reason;
        }
        _blockingChanged = true;
    }

    private void PublishDiagnostics(HumanActivitySnapshot activity, long started)
    {
        if (_blockingChanged)
        {
            _blockingReasons = Array.AsReadOnly(
                _blocked
                    .Values.GroupBy(r => r)
                    .Select(g => new CapsuleerBlockingReason(g.Key, g.Count()))
                    .OrderByDescending(g => g.Agents)
                    .ToArray()
            );
            _blockingChanged = false;
        }
        var status = _queue.Inspect(activity.ElapsedTicks);
        Volatile.Write(
            ref _diagnostics,
            new(
                _population,
                _systems.Count,
                activity.ActiveSystems.Count,
                Current.Count,
                status.Due,
                status.OldestDueTicks is { } due ? activity.ElapsedTicks - due : 0,
                _completed,
                _failures,
                clock.GetElapsedTime(started),
                _blockingReasons,
                _lastFailure
            )
            {
                LastUpdate = _diagnostics.LastUpdate,
                LastPresentation = _diagnostics.LastPresentation,
            }
        );
    }

    private readonly CapsuleerJobQueue _queue = new();
    private readonly Dictionary<long, CapsuleerJob> _jobs = [];
    private readonly Dictionary<long, CapsuleerMiner> _miners = [];
    private readonly Dictionary<long, CapsuleerHauler> _haulers = [];
    private readonly Dictionary<long, long> _ships = [];
    private readonly Dictionary<long, long> _pilotShips = [];
    private readonly Dictionary<long, int> _locations = [];
    private readonly Dictionary<long, HashSet<int>> _routes = [];
    private readonly Dictionary<int, HashSet<long>> _systemAgents = [];

    private void IndexLocation(long pilot, int system)
    {
        if (_locations.TryGetValue(pilot, out int previous) && (!_routes.TryGetValue(pilot, out var route) || !route.Contains(previous)))
        {
            _systemAgents.GetValueOrDefault(previous)?.Remove(pilot);
        }
        _locations[pilot] = system;
        if (!_systemAgents.TryGetValue(system, out var agents))
        {
            agents = [];
            _systemAgents.Add(system, agents);
        }
        agents.Add(pilot);
    }

    private sealed record SavedLocation(long PilotId, int StationId, int? SystemId);

    private bool _loaded;
    private long _populationRevision;
    private LiveWorld? Live => LiveWorld.Find(database.Path);
    private Dictionary<long, CapsuleerJob> Current => Live?.CapsuleerJobs ?? _jobs;

    internal void Prepare()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        CapsuleerTrafficRoutes.Prepare(catalog);
        using var owner = synchronization.Enter("traffic startup");
        Load();
    }

    private void Load(int? preparedSystem = null, long? preparedPilot = null)
    {
        if (preparedSystem is null && preparedPilot is null && _loaded && _populationRevision == (Live?.CapsuleerRevision ?? 0))
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        bool incremental = _loaded && (preparedSystem is not null || preparedPilot is not null);
        var agentQuery = world.Set<SavedCapsuleerAgent>().AsNoTracking();
        if (incremental)
        {
            agentQuery = preparedPilot is { } pilotId
                ? agentQuery.Where(a => a.PilotId == pilotId)
                : agentQuery.Where(a => a.CommunitySystemId == preparedSystem);
        }
        var agents = agentQuery.OrderBy(a => a.PilotId).ToArray();
        var pilotIds = agents.Select(a => a.PilotId).ToArray();
        var jobs = incremental
            ? world
                .Set<SavedCapsuleerJob>()
                .Where(j => pilotIds.Contains(j.PilotId))
                .Select(j => new CapsuleerJob(j.PilotId, j.Revision, j.Sequence, j.DueTicks, j.Action))
                .ToArray()
            : CapsuleerPopulationStore.Jobs(world);
        foreach (
            int system in world.Set<SavedCapsuleerSystem>().Where(s => !incremental || s.SystemId == preparedSystem).Select(s => s.SystemId)
        )
        {
            _systems.Register(system);
        }
        var locations = (
            from agent in agentQuery
            join pilot in world.Pilots on agent.PilotId equals pilot.Id
            select new SavedLocation(
                agent.PilotId,
                pilot.ActiveShip.StationId,
                pilot.ActiveShip.Position == null ? null : pilot.ActiveShip.Position.SystemId
            )
        ).ToArray();
        var stations = locations.Where(l => l.SystemId is null).Select(l => l.StationId).Distinct().ToArray();
        using CatalogDbContext catalog = new(database.CatalogPath);
        var systems = stations.ToDictionary(id => id, id => catalog.RequireStation(id).SolarSystemId);
        var laneAgents = agents
            .Where(a => a.Career == CapsuleerCareer.Hauling && catalog.Definitions.Gates.ContainsKey(a.Slot))
            .Select(a => a.PilotId)
            .ToHashSet();
        foreach (
            var route in world.Set<SavedCapsuleerHauler>().AsNoTracking().Where(r => !incremental || pilotIds.Contains(r.PilotId)).ToArray()
        )
        {
            var progress = Live?.HaulerCheckpoints.GetValueOrDefault(route.PilotId)?.Work ?? HaulerCheckpoint.Read(route.Progress);
            if (laneAgents.Contains(route.PilotId) || progress?.TrafficLane is not null)
            {
                _travelers.Add(route.PilotId);
            }
            if (progress is { Activity: HaulerActivity.Inbound, InboundPaused: true })
            {
                var job = Current.GetValueOrDefault(route.PilotId) ?? jobs.Single(value => value.PilotId == route.PilotId);
                long elapsed = world.Set<SavedCapsuleerClock>().Single().ElapsedTicks;
                long delay = Math.Max(0, elapsed - progress.ActiveTicks);
                var resumed = progress with
                {
                    InboundPaused = false,
                    ActiveTicks = progress.ActiveTicks + delay,
                    ClockOffsetTicks = progress.ClockOffsetTicks + delay,
                    DueTicks = progress.DueTicks + delay,
                };
                using CapsuleerOperation operation = new(database, job, marketSignal);
                var next = job.Next(resumed.DueTicks, job.Action);
                operation.Commit(next, null, new HaulerCheckpoint(route.PilotId, resumed));
                Current[route.PilotId] = next;
            }
            if (progress?.Journey is null)
            {
                _legacyTravelers.Add(route.PilotId);
            }
            if (progress?.Journey is not null)
            {
                _travelers.Add(route.PilotId);
            }
            var systemsOnRoute = progress?.Journey?.Systems ?? CapsuleerTrafficFlight.Route(route, catalog).Systems;
            IndexRoute(route.PilotId, systemsOnRoute);
        }
        foreach (var location in locations)
        {
            int system = location.SystemId ?? systems[location.StationId];
            IndexLocation(location.PilotId, system);
            _systems.Register(system);
        }
        foreach (var job in jobs)
        {
            Current.TryAdd(job.PilotId, job);
            Live?.ProjectedCapsuleerJobs.TryAdd(job.PilotId, job);
        }
        foreach (var agent in agents)
        {
            if (agent.Career == CapsuleerCareer.Mining && !_miners.ContainsKey(agent.PilotId))
            {
                _miners.Add(agent.PilotId, miners.Create(agent.PilotId));
            }
            else if (agent.Career == CapsuleerCareer.Hauling && !_haulers.ContainsKey(agent.PilotId))
            {
                _haulers.Add(agent.PilotId, haulers.Create(agent.PilotId));
            }
        }
        if (!incremental)
        {
            _queue.Restore(_running.Select(pilot => Current[pilot]));
        }
        _populationRevision = Live?.CapsuleerRevision ?? 0;
        _population = population.Summary();
        _loaded = true;
    }

    public CapsuleerBatch Run(int destinyStamp, int maximumActions = CapsuleerJobQueue.ActionsPerTurn)
    {
        long started = clock.GetTimestamp();
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            _destinyStamp = destinyStamp;
            var activity = presence.Read();
            long throughTicks = activity.ElapsedTicks;
            var pilots = _turnPilots;
            Dictionary<long, MinerObservation> mining = [];
            Dictionary<long, HaulerObservation> hauling = [];
            try
            {
                Load();
                Reconcile(activity);
                pilots.Clear();
                pilots.UnionWith(_running);
                var turn = _queue.Run(
                    throughTicks,
                    job =>
                    {
                        if (Current.GetValueOrDefault(job.PilotId) != job)
                        {
                            throw new InvalidOperationException("A stale capsuleer job cannot execute.");
                        }
                        CapsuleerJob next;
                        Live?.ObserveTrafficJob(job, _locations[job.PilotId]);
                        if (_miners.TryGetValue(job.PilotId, out var miner))
                        {
                            using var phase = SessionPerformance.Phase("traffic.minerAction");
                            bool background = !activity.ActiveSystems.Contains(_locations[job.PilotId]);
                            next = miner.Execute(job, destinyStamp, activity.ElapsedTicks, activity.Resumes, background);
                            Current[job.PilotId] = next;
                            _completed++;
                            IndexLocation(job.PilotId, miner.SystemId);
                            _systems.Register(miner.SystemId);
                            var observation = miner.Read(includeHealth: !background)!;
                            mining[job.PilotId] = observation;
                            Blocking(job.PilotId, observation.WaitingReason);
                            _pendingMiners[job.PilotId] = observation;
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else if (_haulers.TryGetValue(job.PilotId, out var hauler))
                        {
                            using var phase = SessionPerformance.Phase("traffic.haulerAction");
                            bool background = !activity.ActiveSystems.Contains(_locations[job.PilotId]);
                            next = hauler.Execute(job, destinyStamp, background);
                            Current[job.PilotId] = next;
                            _completed++;
                            IndexJourney(job.PilotId, hauler);
                            IndexLocation(job.PilotId, hauler.SystemId);
                            _systems.Register(hauler.SystemId);
                            var observation = hauler.Read()!;
                            hauling[job.PilotId] = observation;
                            Blocking(job.PilotId, observation.Work.WaitingReason);
                            _pendingHaulers[job.PilotId] = observation;
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else
                        {
                            throw new InvalidDataException("The capsuleer job has no supported role.");
                        }
                        Current[job.PilotId] = next;
                        if (
                            !_travelers.Contains(job.PilotId)
                            && !activity.ActiveSystems.Contains(_locations[job.PilotId])
                            && next.Action != CapsuleerAction.GateTransfer
                        )
                        {
                            pilots.Remove(job.PilotId);
                        }
                        return next;
                    },
                    clock,
                    pilots,
                    useCanonicalQueue: true,
                    maximumActions: maximumActions
                );
                Reconcile(activity);
                if (turn.OldestDueTicks is null)
                {
                    Settle(Math.Max(_settledTicks, throughTicks));
                }
                return new(turn, mining.Values.ToArray(), hauling.Values.ToArray());
            }
            catch (Exception error)
            {
                _failures++;
                _lastFailure = error.Message;
                // A committed action can precede a failed observation or another role.
                // Rebuild from canonical revisions so retry cannot replay that action.
                _queue.Restore(_running.Select(pilot => Current[pilot]));
                throw;
            }
            finally
            {
                PublishDiagnostics(activity, started);
            }
        }
    }

    private void Reconcile(HumanActivitySnapshot activity)
    {
        foreach (int system in activity.ActiveSystems)
        {
            if (!_visited.Contains(system))
            {
                var prepared = new CapsuleerProvisioner(database, clock).EnsureSystem(system);
                _visited.Add(system);
                if (prepared.CreatedCommunities > 0)
                {
                    Load(system);
                }
                else
                {
                    _systems.Register(system);
                    _populationRevision = Live?.CapsuleerRevision ?? 0;
                    _population = population.Summary();
                }
            }
        }
        foreach (var state in _systems.Begin(activity.ActiveSystems))
        {
            if (state.Mode == SystemSimulationMode.Deactivating)
            {
                _activated.Remove(state.SystemId);
                _visited.Remove(state.SystemId);
                Live?.Combat?.ReleaseObserverContacts(state.SystemId);
            }
            if (state.Mode == SystemSimulationMode.Activating)
            {
                _activated.Add(state.SystemId);
                foreach (long pilot in _systemAgents.GetValueOrDefault(state.SystemId) ?? [])
                {
                    if (_locations.GetValueOrDefault(pilot) == state.SystemId)
                    {
                        WakePilot(pilot, activity.ElapsedTicks, requireElapsedTime: true);
                    }
                }
            }
        }
        foreach (int system in _activated)
        {
            foreach (long pilot in _systemAgents.GetValueOrDefault(system) ?? [])
            {
                if (_legacyTravelers.Contains(pilot) && _haulers.TryGetValue(pilot, out var legacy))
                {
                    if (!_running.Contains(pilot) && legacy.Read()!.Work.Activity != HaulerActivity.Jumping)
                    {
                        Current[pilot] = legacy.Resume(Current[pilot], activity.ElapsedTicks);
                        _queue.Publish(Current[pilot]);
                    }
                    _travelers.Add(pilot);
                    _legacyTravelers.Remove(pilot);
                }
                if (
                    _running.Contains(pilot)
                    || _travelers.Contains(pilot)
                    || !_haulers.TryGetValue(pilot, out var hauler)
                    || activity.ActiveSystems.Contains(_locations[pilot])
                )
                {
                    continue;
                }
                // Only an adjacent saved route leg can be accounted for while
                // unobserved; the hauler validates its hull and travel restrictions.
                const int reappearanceMinutes = 15;
                var job = Current[pilot];
                if (
                    activity.ElapsedTicks - job.DueTicks < TimeSpan.FromMinutes(reappearanceMinutes).Ticks
                    || (pilot + activity.ElapsedTicks / TimeSpan.FromHours(1).Ticks) % 2 != 0
                )
                {
                    continue;
                }
                Current[pilot] = hauler.Reappear(job, activity.ElapsedTicks, system);
                IndexLocation(pilot, hauler.SystemId);
            }
        }
        var inbound = ReconcileInbound(activity);
        var running = _nextRunning;
        running.Clear();
        foreach (int system in activity.ActiveSystems)
        {
            foreach (long pilot in _systemAgents.GetValueOrDefault(system) ?? [])
            {
                if (activity.ActiveSystems.Contains(_locations[pilot]))
                {
                    running.Add(pilot);
                }
            }
        }
        running.UnionWith(inbound);
        foreach (long pilot in running.Except(_running))
        {
            if (_travelers.Contains(pilot) && !_haulers[pilot].IsRatting)
            {
                continue;
            }
            var job = Current[pilot];
            Current[pilot] = _miners.TryGetValue(pilot, out var miner)
                ? miner.Resume(job, activity.ElapsedTicks)
                : _haulers[pilot].Resume(job, activity.ElapsedTicks);
        }
        // A witnessed gate departure is one durable transfer, even if its source
        // loses its last observer before the destination becomes visible.
        running.UnionWith(_running.Where(pilot => Current[pilot].Action == CapsuleerAction.GateTransfer && !_haulers[pilot].IsInbound));
        foreach (long pilot in _running.Except(running))
        {
            _queue.Remove(pilot);
            // Release navigation, contacts and fitting references. The immutable
            // checkpoint retains the encounter and its paid work for re-entry.
            if (_miners.ContainsKey(pilot))
            {
                _miners[pilot] = miners.Create(pilot);
            }
            else
            {
                _haulers[pilot] = haulers.Create(pilot);
            }
        }
        foreach (long pilot in running.Except(_running))
        {
            _queue.Publish(Current[pilot]);
        }
        _nextRunning = _running;
        _running = running;
    }

    private void Settle(long throughTicks)
    {
        _settledTicks = throughTicks;
        _systems.SettleAll(throughTicks);
    }

    public IReadOnlyList<SystemSimulationState> Systems()
    {
        lock (synchronization.Gate)
        {
            Load();
            return _systems.Read();
        }
    }

    public void Synchronize(Action action) => SynchronizeCore(action);

    public void Synchronize(int systemId, Action action) => SynchronizeCore(action);

    private void SynchronizeCore(Action action)
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            long? previousDue = _queue.NextDueTicks;
            action();
            using var reconciliation = SessionLatency.Measure(SessionLatencyPhase.TrafficReconcile);
            Load();
            var activity = presence.Read();
            Reconcile(activity);
            // Publish newly prepared metadata before observer frames begin.
            Load();
            Settle(activity.ElapsedTicks);
            if (_queue.NextDueTicks != previousDue)
            {
                presence.Wake();
            }
        }
    }

    public ValueTask SynchronizeAsync(Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SynchronizeCore(action);
        return ValueTask.CompletedTask;
    }

    public ValueTask SynchronizeAsync(int systemId, Action action, CancellationToken cancellationToken = default) =>
        SynchronizeAsync(action, cancellationToken);

    public CapsuleerBatch Capture()
    {
        lock (synchronization.Gate)
        {
            Load();
            Reconcile(presence.Read());
            Settle(presence.Read().ElapsedTicks);
            var active = _systems.Observed().Where(s => s.Mode == SystemSimulationMode.Active).Select(s => s.SystemId).ToHashSet();
            foreach (
                var location in active
                    .SelectMany(system => _systemAgents.GetValueOrDefault(system) ?? [])
                    .Distinct()
                    .Where(pilot => active.Contains(_locations[pilot]))
                    .Select(pilot => new KeyValuePair<long, int>(pilot, _locations[pilot]))
            )
            {
                if (_miners.TryGetValue(location.Key, out var miner))
                {
                    var observation = miner.Read()!;
                    _pendingMiners[observation.Pilot.PilotId] = observation;
                }
                if (_haulers.TryGetValue(location.Key, out var hauler))
                {
                    var observation = hauler.Read()!;
                    _pendingHaulers[observation.Pilot.PilotId] = observation;
                }
            }
            _activated.Clear();
            CapsuleerBatch batch = new(
                new(0, Current.Count, null, false),
                _pendingMiners.Values.ToArray(),
                _pendingHaulers.Values.ToArray()
            )
            {
                Revision = _presentationRevision,
                Systems = _systems.Observed(),
            };
            _pendingMiners.Clear();
            _pendingHaulers.Clear();
            // Health reads retain exact recharge and expiry. Reuse only equal visible snapshots.
            if (
                _lastPresentation is { } previous
                && previous.Miners.SequenceEqual(batch.Miners)
                && previous.Haulers.SequenceEqual(batch.Haulers)
                && previous
                    .Systems.Select(s => (s.SystemId, s.Mode, s.Generation))
                    .SequenceEqual(batch.Systems.Select(s => (s.SystemId, s.Mode, s.Generation)))
            )
            {
                if (previous.Systems.SequenceEqual(batch.Systems))
                {
                    return previous;
                }
                batch = batch with { Revision = previous.Revision };
            }
            else
            {
                batch = batch with { Revision = checked(++_presentationRevision) };
            }
            _lastPresentation = batch;
            return batch;
        }
    }

    public IReadOnlyList<CapsuleerJob> Jobs()
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            Load();
            return Current.Values.OrderBy(j => j.DueTicks).ThenBy(j => j.PilotId).ToArray();
        }
    }

    public CapsuleerWakePlan NextWake()
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            var activity = presence.Read();
            return new(CapsuleerScheduleRules.WakeDelay(_queue.NextDueTicks, activity.ElapsedTicks, activity.Active), presence.Changed);
        }
    }

    public MinerObservation? Miner(long pilotId)
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            Load();
            return _miners.GetValueOrDefault(pilotId)?.Read();
        }
    }

    public HaulerObservation? Hauler(long pilotId)
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            Load();
            return _haulers.GetValueOrDefault(pilotId)?.Read();
        }
    }

    private void RegisterShip(long pilot, long ship)
    {
        if (_pilotShips.TryGetValue(pilot, out long previous) && previous != ship)
        {
            _ships.Remove(previous);
        }
        _pilotShips[pilot] = ship;
        _ships[ship] = pilot;
    }

    public void ObserveAggression(long shipId)
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            if (_ships.TryGetValue(shipId, out long pilot))
            {
                _miners.GetValueOrDefault(pilot)?.ObserveAggression(shipId);
                _haulers.GetValueOrDefault(pilot)?.ObserveAggression(shipId);
                WakePilot(pilot, presence.Read().ElapsedTicks);
                presence.Wake();
            }
        }
    }

    private void WakePilot(long pilot, long elapsedTicks, bool requireElapsedTime = false)
    {
        if (!_running.Contains(pilot) || !Current.TryGetValue(pilot, out var job) || job.DueTicks <= elapsedTicks)
        {
            return;
        }
        if (requireElapsedTime)
        {
            long sampled = _miners.TryGetValue(pilot, out var miner)
                ? miner.Read(includeHealth: false)!.Work.ActiveTicks
                : _haulers[pilot].Read()!.Work.ActiveTicks;
            if (sampled >= elapsedTicks)
            {
                return;
            }
        }
        // An external event revises the pending job without consuming an action or paid work.
        var next = job with
        {
            Revision = checked(job.Revision + 1),
            DueTicks = elapsedTicks,
        };
        Current[pilot] = next;
        _queue.Publish(next);
        Live?.ObjectsChanged();
    }
}
