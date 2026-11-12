using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
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
    PilotMarketStore market
) : ICapsuleerScheduler, ICapsuleerSimulation, ICapsuleerHauling, ICapsuleerHandoff, ICapsuleerDiagnostics, ICapsuleerUpdateDiagnostics
{
    private readonly SystemSimulationRegistry _systems = new();
    private readonly Dictionary<long, MinerObservation> _pendingMiners = [];
    private readonly Dictionary<long, HaulerObservation> _pendingHaulers = [];
    private HashSet<long> _running = [];
    private HashSet<long> _nextRunning = [];
    private readonly HashSet<int> _visited = [];
    private long _presentationRevision;
    private CapsuleerBatch? _lastPresentation;
    private long _settledTicks;
    private CapsuleerRuntimeSummary _diagnostics = CapsuleerRuntimeSummary.Empty;
    private CapsuleerPopulationSummary _population = new(0, 0, 0, 0, 0);
    private readonly Dictionary<long, string> _blocked = [];
    private IReadOnlyList<CapsuleerBlockingReason> _blockingReasons = [];
    private bool _blockingChanged;
    private bool _membershipDirty = true;
    private int[] _reconciledSystems = [];
    private IReadOnlyList<SelectedHumanPilot> _reconciledPilots = [];
    private long _reconciledResumes = -1;
    private IReadOnlyList<WatchedTrafficGate> _reconciledGates = [];
    internal long Reconciliations { get; private set; }
    internal long PopulationRowsLoaded { get; private set; }
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMilliseconds(250);
    private long? _diagnosticTimestamp;
    private CapsuleerQueueStatus _queueStatus = new(0, null);
    private long _completed;
    private long _failures;
    private string? _lastFailure;

    public CapsuleerRuntimeSummary Read() => Volatile.Read(ref _diagnostics);

    public void RecordUpdate(CapsuleerUpdateTiming timing)
    {
        using (synchronization.Enter("CapsuleerScheduler"))
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
        // Counting a large overdue prefix for every single action makes backlog recovery quadratic.
        // Queue execution and its earliest deadline remain exact between diagnostic samples.
        long sampledAt = clock.GetTimestamp();
        if (
            _diagnosticTimestamp is not { } previousSample
            || clock.GetElapsedTime(previousSample, sampledAt) >= DiagnosticInterval
            || _queue.NextDueTicks is null
            || _queue.NextDueTicks > activity.ElapsedTicks
            || !activity.Active
            || _failures != _diagnostics.Failures
        )
        {
            using var inspection = SessionPerformance.Phase("traffic.queueDiagnostics");
            _queueStatus = _queue.Inspect(activity.ElapsedTicks);
            _diagnosticTimestamp = sampledAt;
        }
        var status = _queueStatus;
        long? oldest = _queue.NextDueTicks is { } first && first <= activity.ElapsedTicks ? first : null;
        var elapsed = clock.GetElapsedTime(started);
        var overrun = elapsed > CapsuleerJobQueue.ExecutionBudget ? elapsed - CapsuleerJobQueue.ExecutionBudget : TimeSpan.Zero;
        Volatile.Write(
            ref _diagnostics,
            new(
                _population,
                _systems.Count,
                activity.ActiveSystems.Count,
                Current.Count,
                status.Due,
                oldest is { } due ? activity.ElapsedTicks - due : 0,
                _completed,
                _failures,
                clock.GetElapsedTime(started),
                _blockingReasons,
                _lastFailure
            )
            {
                ScheduledAgents = _running.Count,
                LastUpdate = _diagnostics.LastUpdate,
                LastPresentation = _diagnostics.LastPresentation,
                BudgetOverruns = _diagnostics.BudgetOverruns + (overrun > TimeSpan.Zero ? 1 : 0),
                MaximumTurnOverrun = overrun > _diagnostics.MaximumTurnOverrun ? overrun : _diagnostics.MaximumTurnOverrun,
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
    private readonly Dictionary<int, HashSet<long>> _systemAgents = [];

    private void IndexLocation(long pilot, int system)
    {
        if (
            _locations.TryGetValue(pilot, out int current)
            && current == system
            && _systemAgents.TryGetValue(system, out var existing)
            && existing.Contains(pilot)
        )
        {
            return;
        }
        _membershipDirty = true;
        int? previousRuntime = _locations.TryGetValue(pilot, out int savedSystem) ? savedSystem : null;
        Live?.AssignCapsuleer(pilot, previousRuntime, system, _miners.GetValueOrDefault(pilot), _haulers.GetValueOrDefault(pilot));
        if (_locations.TryGetValue(pilot, out int previous))
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

    private HashSet<long> _suspended = [];
    private bool _loaded;
    private long _populationRevision;
    private readonly Dictionary<long, int> _communities = [];

    private sealed record PilotPause(long ElapsedTicks, long? Timestamp = null);

    private readonly Dictionary<long, PilotPause> _paused = [];
    private readonly Dictionary<long, long> _resuming = [];
    private readonly HashSet<long> _newPilots = [];
    private LiveWorld? Live => LiveWorld.Find(database.Path);
    private Dictionary<long, CapsuleerJob> Current => Live?.CapsuleerJobs ?? _jobs;

    internal void Prepare()
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        CapsuleerTrafficRoutes.Prepare(catalog);
    }

    internal void PopulationPrepared(int system, long? pilot = null)
    {
        using var owner = synchronization.Enter("population scheduler registration");
        if (pilot is null || presence.Read().ActiveSystems.Contains(system))
        {
            Load(system, pilot);
            presence.Wake();
        }
    }

    private void Load(int? preparedSystem = null, long? preparedPilot = null)
    {
        // The common unchanged path must not allocate a resident transaction view.
        if (preparedSystem is null && preparedPilot is null && _loaded && _populationRevision == (Live?.CapsuleerRevision ?? 0))
        {
            return;
        }
        WorldActionScope.Inspect(database, world => Load(world, preparedSystem, preparedPilot));
    }

    private void Load(WorldAction world, int? preparedSystem, long? preparedPilot)
    {
        if (
            preparedSystem is null
            && preparedPilot is null
            && _loaded
            && Live is { } live
            && live.CapsuleerMetadataReset <= _populationRevision
        )
        {
            RefreshChangedPopulation(live);
            return;
        }
        using var loading = SessionPerformance.Phase("traffic.load");
        _membershipDirty = true;
        _suspended = world.Set<SavedCapsuleerSuspension>().Select(s => s.PilotId).ToHashSet();
        if (preparedSystem is { } system)
        {
            LoadSystem(world, system, preparedPilot);
        }
        else if (Live is null)
        {
            Register(world, world.Set<SavedCapsuleerAgent>().AsNoTracking().OrderBy(a => a.PilotId).ToArray());
        }
        else
        {
            foreach (int observed in presence.Read().ActiveSystems)
            {
                LoadSystem(world, observed, null);
            }
        }
        _populationRevision = Live?.CapsuleerRevision ?? 0;
        _population = population.Summary();
        _loaded = true;
    }

    private void LoadSystem(WorldAction world, int system, long? pilot)
    {
        _systems.Register(system);
        if (pilot is { } founder)
        {
            Register(world, world.Set<SavedCapsuleerAgent>().Where(a => a.PilotId == founder).ToArray(), founderSystem: system);
            return;
        }
        var stations = database.Catalog.StationsInSystem(system).Select(station => station.Id).ToArray();
        var localShips = world.Positions.Where(position => position.SystemId == system).Select(position => position.ShipId).ToHashSet();
        foreach (long ship in world.Ships.Where(ship => Enumerable.Contains(stations, ship.StationId)).Select(ship => ship.Id))
        {
            if (world.Positions.Find(ship)?.SystemId is null)
            {
                localShips.Add(ship);
            }
        }
        var ships = localShips.ToArray();
        var pilots = world.Pilots.Where(p => Enumerable.Contains(ships, p.ActiveShipId)).Select(p => p.Id).ToArray();
        var agents = world
            .Set<SavedCapsuleerAgent>()
            .Where(agent => Enumerable.Contains(pilots, agent.PilotId))
            .OrderBy(a => a.PilotId)
            .ToArray();
        Register(world, agents);
    }

    private void Register(WorldAction world, IReadOnlyList<SavedCapsuleerAgent> agents, int? founderSystem = null)
    {
        PopulationRowsLoaded += agents.Count;
        foreach (var agent in agents)
        {
            long pilot = agent.PilotId;
            long ship = world.Pilots.Find(pilot)!.ActiveShipId;
            int system =
                world.Positions.Find(ship)?.SystemId ?? database.Catalog.FindStation(world.Ships.Find(ship)!.StationId)!.SolarSystemId;
            if (founderSystem is int occupied && system != occupied)
            {
                continue;
            }
            _communities[pilot] = agent.CommunitySystemId;
            if (!Current.ContainsKey(pilot))
            {
                var saved = world.Set<SavedCapsuleerJob>().Find(pilot)!;
                var job = new CapsuleerJob(pilot, saved.Revision, saved.Sequence, saved.DueTicks, saved.Action);
                Current.Add(pilot, job);
                Live?.ProjectedCapsuleerJobs.TryAdd(pilot, job);
                // Older saves recorded the last settled tick in this row. It is also the pause cutoff for new saves.
                if (world.Set<SavedCapsuleerCoarsePilot>().Find(pilot) is { } paused)
                {
                    _paused[pilot] = new(paused.SettledTicks);
                }
                if (founderSystem is null)
                {
                    _newPilots.Add(pilot);
                }
            }
            if (agent.Career == CapsuleerCareer.Mining && !_miners.ContainsKey(pilot))
            {
                _miners.Add(pilot, miners.Create(pilot));
            }
            else if (agent.Career == CapsuleerCareer.Hauling && !_haulers.ContainsKey(pilot))
            {
                _haulers.Add(pilot, haulers.Create(pilot));
            }
            IndexLocation(pilot, system);
            RegisterShip(pilot, ship);
        }
    }

    public CapsuleerBatch Run(int destinyStamp, int maximumActions = CapsuleerJobQueue.ActionsPerTurn) =>
        RunPrepared(destinyStamp, maximumActions);

    private CapsuleerBatch RunCore(
        int destinyStamp,
        int maximumActions,
        CapsuleerHauler.PreparedJourney? preparedJourney = null,
        bool measured = false,
        long? turnStarted = null
    )
    {
        long started = turnStarted ?? clock.GetTimestamp();
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            var activity = presence.Read();
            long throughTicks = activity.ElapsedTicks;
            Dictionary<long, MinerObservation> mining = [];
            Dictionary<long, HaulerObservation> hauling = [];
            try
            {
                Load();
                Reconcile(activity);
                if (!activity.Active)
                {
                    return new(new(0, Current.Count, null, false), [], []);
                }
                using var queuePhase = SessionPerformance.Phase("traffic.queueExecution");
                var turn = _queue.Run(
                    throughTicks,
                    job =>
                    {
                        long pilot = job.PilotId;
                        int system = _locations[pilot];
                        if (!activity.ActiveSystems.Contains(system))
                        {
                            throw new InvalidOperationException("A capsuleer job requires a player in its system.");
                        }
                        using var systemContext = Live?.UseSystem(system);
                        if (_resuming.TryGetValue(pilot, out long entry))
                        {
                            var saved = Current[pilot];
                            long paused = _paused.GetValueOrDefault(pilot)?.ElapsedTicks ?? 0;
                            var resumed = _miners.TryGetValue(pilot, out var pausedMiner)
                                ? pausedMiner.Resume(saved, entry, paused)
                                : _haulers[pilot].Resume(saved, entry, paused);
                            Current[pilot] = resumed;
                            _resuming.Remove(pilot);
                            _paused.Remove(pilot);
                            return resumed;
                        }
                        if (Current.GetValueOrDefault(pilot) != job)
                        {
                            throw new InvalidOperationException("A stale capsuleer job cannot execute.");
                        }
                        using var actionPhase = SessionPerformance.IsActive
                            ? SessionPerformance.Phase($"traffic.action.{job.Action}")
                            : null;
                        using var measurement = measured
                            ? null
                            : Live?.TrafficActions.Measure(
                                job,
                                [_locations[job.PilotId]],
                                () => (Live!.CheckpointCopies, Live.CheckpointCopying)
                            );
                        CapsuleerJob next;
                        Live?.ObserveTrafficJob(job, _locations[job.PilotId]);
                        if (_miners.TryGetValue(job.PilotId, out var miner))
                        {
                            using var phase = SessionPerformance.Phase("traffic.minerAction");
                            next = miner.Execute(job, destinyStamp, activity.ElapsedTicks, activity.Resumes);
                            Current[job.PilotId] = next;
                            _completed++;
                            IndexLocation(job.PilotId, miner.SystemId);
                            _systems.Register(miner.SystemId);
                            var observation = miner.Read()!;
                            mining[job.PilotId] = observation;
                            Blocking(job.PilotId, observation.WaitingReason);
                            _pendingMiners[job.PilotId] = observation;
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else if (_haulers.TryGetValue(job.PilotId, out var hauler))
                        {
                            using var phase = SessionPerformance.Phase("traffic.haulerAction");
                            next = hauler.Execute(job, destinyStamp, preparedJourney: preparedJourney);
                            Current[job.PilotId] = next;
                            _completed++;
                            IndexLocation(job.PilotId, hauler.SystemId);
                            _systems.Register(hauler.SystemId);
                            using var observationContext = Live?.UseSystem(hauler.SystemId);
                            var observation = hauler.Read(includeHealth: activity.ActiveSystems.Contains(hauler.SystemId))!;
                            hauling[job.PilotId] = observation;
                            Blocking(job.PilotId, observation.Work.WaitingReason);
                            _pendingHaulers[job.PilotId] = observation;
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else
                        {
                            throw new InvalidDataException("The capsuleer job has no supported role.");
                        }
                        if (!activity.ActiveSystems.Contains(_locations[pilot]))
                        {
                            PausePilot(pilot, throughTicks);
                            _running.Remove(pilot);
                        }
                        return next;
                    },
                    clock,
                    _running,
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
                _queue.Restore(_running.Select(ScheduledJob));
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
        var policy = _populationSettings.Settings.Capsuleers;
        IReadOnlyList<WatchedTrafficGate> gates =
            policy.Enabled && policy.GateVisitors > 0 && activity.ActiveSystems.Count > 1
                ? gateWatch.Read(activity).Where(gate => activity.ActiveSystems.Contains(gate.SourceSystemId)).ToArray()
                : [];
        if (
            !_membershipDirty
            && _reconciledResumes == activity.Resumes
            && _reconciledSystems.SequenceEqual(activity.ActiveSystems)
            && _reconciledPilots.SequenceEqual(activity.Pilots)
            && _reconciledGates.SequenceEqual(gates)
        )
        {
            return;
        }
        using var phase = SessionPerformance.Phase("traffic.membership");
        Reconciliations++;
        foreach (int system in activity.ActiveSystems)
        {
            if (!_visited.Contains(system))
            {
                if (Live is null)
                {
                    using CapsuleerProvisioner provisioner = new(database, clock);
                    provisioner.EnsureSystem(system);
                }
                Load(system);
                _visited.Add(system);
            }
        }
        var transitions = _systems.Begin(activity.ActiveSystems);
        foreach (var state in transitions)
        {
            if (state.Mode == SystemSimulationMode.Deactivating)
            {
                _visited.Remove(state.SystemId);
                Live?.CombatForSystem(state.SystemId)?.ReleaseObserverContacts(state.SystemId);
            }
        }
        ReconcileInbound(gates);
        var running = _nextRunning;
        running.Clear();
        foreach (int system in activity.ActiveSystems)
        {
            foreach (long pilot in _systemAgents.GetValueOrDefault(system) ?? [])
            {
                if (!_suspended.Contains(pilot))
                {
                    running.Add(pilot);
                }
            }
        }
        foreach (long pilot in _running.Except(running))
        {
            PausePilot(pilot, activity.ElapsedTicks);
            _queue.Remove(pilot);
        }
        foreach (long pilot in running.Except(_running))
        {
            bool firstEntry = _newPilots.Remove(pilot);
            if (_paused.TryGetValue(pilot, out var pause))
            {
                if (pause.ElapsedTicks < activity.ElapsedTicks || pause.Timestamp != clock.GetTimestamp())
                {
                    _resuming[pilot] = activity.ElapsedTicks;
                }
                else
                {
                    _paused.Remove(pilot);
                }
            }
            else if (firstEntry && activity.ElapsedTicks > 0)
            {
                _resuming[pilot] = activity.ElapsedTicks;
            }
            Live?.AssignCapsuleer(pilot, null, _locations[pilot], _miners.GetValueOrDefault(pilot), _haulers.GetValueOrDefault(pilot));
            _queue.Publish(ScheduledJob(pilot));
        }
        _nextRunning = _running;
        _running = running;
        _membershipDirty = false;
        UpdateDetailRetention(activity, transitions);
        _reconciledSystems = activity.ActiveSystems.ToArray();
        _reconciledPilots = activity.Pilots;
        _reconciledResumes = activity.Resumes;
        _reconciledGates = gates.ToArray();
        if (_diagnostics.ScheduledAgents != running.Count)
        {
            Volatile.Write(ref _diagnostics, _diagnostics with { ScheduledAgents = running.Count });
        }
    }

    private CapsuleerJob ScheduledJob(long pilot) =>
        _resuming.TryGetValue(pilot, out long entry) ? Current[pilot] with { DueTicks = entry } : Current[pilot];

    private void PausePilot(long pilot, long elapsed)
    {
        // If entry was interrupted before its reschedule committed, keep the original pause.
        if (_resuming.Remove(pilot))
        {
            if (!_paused.ContainsKey(pilot))
            {
                _newPilots.Add(pilot);
            }
        }
        else
        {
            _paused[pilot] = new(elapsed, clock.GetTimestamp());
            Live?.CapsuleerRoutines.Settle(pilot, elapsed);
        }
        if (_miners.ContainsKey(pilot))
        {
            _miners[pilot] = miners.Create(pilot);
        }
        else
        {
            _haulers[pilot] = haulers.Create(pilot);
        }
        Live?.AssignCapsuleer(pilot, null, _locations[pilot], null, null);
        Blocking(pilot, null);
    }

    private void Settle(long throughTicks)
    {
        _settledTicks = throughTicks;
        _systems.SettleAll(throughTicks);
    }

    public IReadOnlyList<SystemSimulationState> Systems()
    {
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            return _systems.Read();
        }
    }

    public void Synchronize(Action action) => SynchronizeCore(action);

    public void Synchronize(int systemId, Action action) => SynchronizeCore(action, systemId);

    private void SynchronizeCore(Action action, int? systemId = null)
    {
        using var owner = Live?.EnterShared("traffic handoff");
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            long? previousDue = _queue.NextDueTicks;
            action();
            using var reconciliation = SessionLatency.Measure(SessionLatencyPhase.TrafficReconcile);
            Load();
            var activity = presence.Read();
            Reconcile(activity);
            // Publish newly prepared metadata before observer frames begin.
            Load();
            SettleReadySystems(activity);
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

    public ValueTask SynchronizeAsync(int systemId, Action action, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Live is { } live)
        {
            using var context = live.UseSystem(systemId);
            action();
        }
        else
        {
            SynchronizeCore(action, systemId);
        }
        return ValueTask.CompletedTask;
    }

    public IReadOnlyCollection<int> PresentationSystems =>
        _systems.Observed().Select(s => s.SystemId).Concat(presence.Read().ActiveSystems).Distinct().ToArray();

    public CapsuleerBatch Capture()
    {
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            Reconcile(presence.Read());
            var activity = presence.Read();
            // Capture never executes scheduled actions. Due systems remain activating
            // until bounded maintenance turns have settled their saved deadlines.
            SettleReadySystems(activity);
            var active = _systems.Observed().Where(s => s.Mode == SystemSimulationMode.Active).Select(s => s.SystemId).ToHashSet();
            foreach (
                var location in active
                    .SelectMany(system => _systemAgents.GetValueOrDefault(system) ?? [])
                    .Distinct()
                    .Where(pilot => !_suspended.Contains(pilot) && active.Contains(_locations[pilot]))
                    .Select(pilot => new KeyValuePair<long, int>(pilot, _locations[pilot]))
            )
            {
                using var systemContext = Live?.UseSystem(location.Value);
                using var systemOwnership = synchronization.Enter("traffic observation", location.Value);
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
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            return Current.Values.OrderBy(j => j.DueTicks).ThenBy(j => j.PilotId).ToArray();
        }
    }

    public CapsuleerWakePlan NextWake()
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            var activity = presence.Read();
            return new(
                activity.Active ? CapsuleerScheduleRules.WakeDelay(_queue.NextDueTicks, activity.ElapsedTicks, true) : null,
                presence.Changed
            );
        }
    }

    public MinerObservation? Miner(long pilotId)
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            return _miners.GetValueOrDefault(pilotId)?.Read();
        }
    }

    public HaulerObservation? Hauler(long pilotId)
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
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
        if (_locations.TryGetValue(pilot, out int system))
        {
            // The location came from committed admission or a completed role action.
            // Reuse it at SQL boundaries rather than resolving the same membership repeatedly.
            Live?.TrackShip(ship, system, pilot);
        }
    }

    public void ObserveAggression(long shipId)
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            if (_ships.TryGetValue(shipId, out long pilot) && _running.Contains(pilot))
            {
                _miners.GetValueOrDefault(pilot)?.ObserveAggression(shipId);
                _haulers.GetValueOrDefault(pilot)?.ObserveAggression(shipId);
                WakePilot(pilot, presence.Read().ElapsedTicks);
                presence.Wake();
            }
        }
    }

    private void WakePilot(long pilot, long elapsedTicks)
    {
        if (_resuming.ContainsKey(pilot) || !Current.TryGetValue(pilot, out var job) || job.DueTicks <= elapsedTicks)
        {
            return;
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
