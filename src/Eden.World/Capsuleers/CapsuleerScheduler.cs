using Eden.Data.World.Capsuleers;
using Eden.Data.World.Structures;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
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
    PilotMarketStore market,
    NpcPopulationSeeder? npcPopulation = null
)
    : ICapsuleerScheduler,
        ICapsuleerSimulation,
        ICapsuleerHauling,
        ICapsuleerHandoff,
        ICapsuleerDiagnostics,
        ICapsuleerUpdateDiagnostics,
        IDisposable
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
    private readonly Dictionary<string, int> _blockedByReason = new(StringComparer.Ordinal);
    private IReadOnlyList<CapsuleerBlockingReason> _blockingReasons = [];
    private bool _blockingChanged;
    private bool _membershipDirty = true;
    private int[] _reconciledSystems = [];
    private IReadOnlyList<SelectedHumanPilot> _reconciledPilots = [];
    private long _reconciledResumes = -1;
    private IReadOnlyList<WatchedTrafficGate> _reconciledGates = [];
    internal long Reconciliations { get; private set; }
    internal long PopulationRowsLoaded { get; private set; }
    internal long PresentationReads { get; private set; }
    internal int RetainedRoleWorkers => _miners.Count + _haulers.Count;
    private static readonly TimeSpan DiagnosticInterval = TimeSpan.FromMilliseconds(250);
    private long? _diagnosticTimestamp;
    private CapsuleerQueueStatus _queueStatus = new(0, null);
    private long _completed;
    private long _completedMining;
    private long _completedHauling;
    private IReadOnlyList<CapsuleerCareerQueue> _careerQueue = [];
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
        string? previous = _blocked.GetValueOrDefault(pilot);
        if (previous == reason)
        {
            return;
        }
        if (previous is not null)
        {
            int remaining = _blockedByReason[previous] - 1;
            if (remaining == 0)
            {
                _blockedByReason.Remove(previous);
            }
            else
            {
                _blockedByReason[previous] = remaining;
            }
        }
        if (reason is null)
        {
            _blocked.Remove(pilot);
        }
        else
        {
            _blocked[pilot] = reason;
            _blockedByReason[reason] = _blockedByReason.GetValueOrDefault(reason) + 1;
        }
        _blockingChanged = true;
    }

    private IReadOnlyList<CapsuleerBlockingReason> ReadBlockingReasons()
    {
        var reasons = _blockedByReason.Select(pair => new CapsuleerBlockingReason(pair.Key, pair.Value)).ToArray();
        Array.Sort(reasons, static (left, right) => right.Agents.CompareTo(left.Agents));
        for (int index = 1; index < reasons.Length; index++)
        {
            if (reasons[index - 1].Agents != reasons[index].Agents)
            {
                continue;
            }
            // Resolve equal counts in the same order as grouping the per-pilot reasons.
            Dictionary<string, int> firstOccurrences = new(reasons.Length, StringComparer.Ordinal);
            foreach (string reason in _blocked.Values)
            {
                firstOccurrences.TryAdd(reason, firstOccurrences.Count);
                if (firstOccurrences.Count == reasons.Length)
                {
                    break;
                }
            }
            Array.Sort(
                reasons,
                (left, right) =>
                {
                    int count = right.Agents.CompareTo(left.Agents);
                    return count != 0 ? count : firstOccurrences[left.Reason].CompareTo(firstOccurrences[right.Reason]);
                }
            );
            break;
        }
        return Array.AsReadOnly(reasons);
    }

    private void PublishDiagnostics(HumanActivitySnapshot activity, long started)
    {
        if (_blockingChanged)
        {
            _blockingReasons = ReadBlockingReasons();
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
            if (Live?.MeasureBotReplay == true)
            {
                int dueMining = _queue
                    .DueJobs(activity.ElapsedTicks)
                    .Count(job => _registrations[job.PilotId].Career == CapsuleerCareer.Mining);
                _careerQueue =
                [
                    new(CapsuleerCareer.Mining, dueMining, _completedMining),
                    new(CapsuleerCareer.Hauling, _queueStatus.Due - dueMining, _completedHauling),
                ];
            }
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
                _registrations.Count,
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
                WaitingBackgroundStarts = _waitingBackground.Count,
                LastUpdate = _diagnostics.LastUpdate,
                LastPresentation = _diagnostics.LastPresentation,
                BudgetOverruns = _diagnostics.BudgetOverruns + (overrun > TimeSpan.Zero ? 1 : 0),
                MaximumTurnOverrun = overrun > _diagnostics.MaximumTurnOverrun ? overrun : _diagnostics.MaximumTurnOverrun,
                Careers = _careerQueue,
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
    private readonly Dictionary<int, HashSet<long>> _miningCrews = [];

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
        int? previousRuntime = _locations.TryGetValue(pilot, out int savedSystem) ? savedSystem : null;
        _membershipDirty |= _reconciledSystems.Contains(system) || previousRuntime is int prior && _reconciledSystems.Contains(prior);
        Live?.TrackCapsuleer(pilot, previousRuntime, system);
        if (previousRuntime is int previous)
        {
            RemoveMember(_systemAgents, previous, pilot);
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
    private bool _backgroundPilotsLoaded;
    private bool _restoredOperations;
    private long _populationRevision;

    private readonly record struct Registration(int Community, CapsuleerCareer Career, int CrewSystem);

    private readonly Dictionary<long, Registration> _registrations = [];

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
        }
        Load();
        presence.Wake();
    }

    private void Load(int? preparedSystem = null, long? preparedPilot = null)
    {
        // The common unchanged path must not allocate a resident transaction view.
        if (preparedSystem is null && preparedPilot is null && _loaded && _populationRevision == (Live?.CapsuleerRevision ?? 0))
        {
            return;
        }
        WorldActionScope.Inspect(database, world => Load(world, preparedSystem, preparedPilot));
        // Once the private inspection ends, the population store can reuse its committed summary.
        _population = population.Summary();
        if (preparedSystem is null)
        {
            _populationRevision = Live?.CapsuleerRevision ?? 0;
        }
        _loaded = true;
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
            RefreshChangedPopulation(world, live);
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
            _backgroundPilotsLoaded = false;
            foreach (int observed in presence.Read().ActiveSystems)
            {
                LoadSystem(world, observed, null, includeRemote: false);
            }
        }
    }

    private void LoadSystem(WorldAction world, int system, long? pilot, bool includeRemote = true)
    {
        _systems.Register(system);
        if (world.Set<SavedCapsuleerCoarseWork>().Find(system) is { } work)
        {
            Live?.CapsuleerRoutines.RestoreMissing(new(work.SystemId, work.Generation, work.Cursor, work.SettledTicks, work.NextTicks));
        }
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
        var structures = world
            .Filter<SavedStructure>(structure => structure.SystemId == system)
            .Select(structure => (long?)structure.Id)
            .ToArray();
        if (structures.Length > 0)
        {
            foreach (long ship in world.Ships.Where(ship => Enumerable.Contains(structures, ship.StructureId)).Select(ship => ship.Id))
            {
                if (world.Positions.Find(ship)?.SystemId is null)
                {
                    localShips.Add(ship);
                }
            }
        }
        var ships = localShips.ToArray();
        var pilots = world.Pilots.Where(p => Enumerable.Contains(ships, p.ActiveShipId)).Select(p => p.Id).ToHashSet();
        // A visitor slot stores the smaller reciprocal gate ID, plus a stride per visitor.
        // Expand the saved slots up front so the resident query can use its slot index.
        HashSet<int> visitorSlots = [];
        const int stride = TrafficJourneyRules.VisitorSlotStride;
        foreach (var gate in includeRemote ? database.Catalog.GatesInSystem(system) : [])
        {
            int lane = Math.Min(gate.Id, gate.DestinationId);
            if (lane is > 0 and < stride)
            {
                // Include saved visitors admitted under a previous population setting.
                for (long slot = lane; slot <= int.MaxValue; slot += stride)
                {
                    visitorSlots.Add((int)slot);
                }
            }
        }
        var agents = world
            .Set<SavedCapsuleerAgent>()
            .Where(agent =>
                Enumerable.Contains(pilots, agent.PilotId)
                || includeRemote
                    && agent.Career == CapsuleerCareer.Hauling
                    && (agent.CommunitySystemId == system || Enumerable.Contains(visitorSlots, agent.Slot))
            )
            .OrderBy(a => a.PilotId)
            .ToArray();
        Register(world, agents);
    }

    private void Register(WorldAction world, IReadOnlyList<SavedCapsuleerAgent> agents, int? founderSystem = null)
    {
        var live = Live;
        foreach (var agent in agents)
        {
            long pilot = agent.PilotId;
            if (live is not null && _suspended.Contains(pilot))
            {
                continue;
            }
            long ship = world.Pilots.Find(pilot)!.ActiveShipId;
            int system = PilotSystem(world, ship);
            if (founderSystem is int occupied && system != occupied)
            {
                continue;
            }
            PopulationRowsLoaded++;
            if (!_registrations.TryGetValue(pilot, out var registration))
            {
                var organization =
                    live is not null || agent.Career == CapsuleerCareer.Mining ? CapsuleerCorporations.Organization(world, pilot) : null;
                var career = organization?.ActiveWorker ?? agent.Career;
                int crewSystem = agent.CommunitySystemId;
                if (career == CapsuleerCareer.Mining)
                {
                    if (organization?.OperatingStationId is int baseStation)
                    {
                        crewSystem = database.Catalog.FindStation(baseStation)?.SolarSystemId ?? crewSystem;
                    }
                    else if (career != agent.Career)
                    {
                        crewSystem = system;
                    }
                    var crew = JoinMiningCrew(pilot, crewSystem);
                    if (live is not null && crew.Count > 1)
                    {
                        RestoreMiningCrew(world, live, crew);
                    }
                }
                _registrations[pilot] = new(agent.CommunitySystemId, career, crewSystem);
                if (!Current.ContainsKey(pilot))
                {
                    var job = ReadJob(world.Set<SavedCapsuleerJob>().Find(pilot)!);
                    Current.Add(pilot, job);
                    live?.ProjectedCapsuleerJobs.TryAdd(pilot, job);
                }
                // Older saves recorded the last settled tick in this row. It is also the pause cutoff for new saves.
                if (live?.CapsuleerRoutines.Settled.TryGetValue(pilot, out long settled) == true)
                {
                    _paused.TryAdd(pilot, new(settled));
                }
                else if (world.Set<SavedCapsuleerCoarsePilot>().Find(pilot) is { } paused)
                {
                    _paused.TryAdd(pilot, new(paused.SettledTicks));
                    live?.CapsuleerRoutines.RestoreMissing(pilot, paused.SettledTicks);
                }
                if (founderSystem is null)
                {
                    _newPilots.Add(pilot);
                }
            }
            else if (registration.Community != agent.CommunitySystemId)
            {
                _registrations[pilot] = registration with { Community = agent.CommunitySystemId };
            }
            IndexLocation(pilot, system);
            RegisterShip(pilot, ship);
        }
    }

    private static CapsuleerJob ReadJob(SavedCapsuleerJob job) => new(job.PilotId, job.Revision, job.Sequence, job.DueTicks, job.Action);

    private int PilotSystem(WorldAction world, long shipId)
    {
        if (world.Positions.Find(shipId)?.SystemId is int system)
        {
            return system;
        }
        var ship = world.Ships.Find(shipId)!;
        return ship.StructureId is long structure
            ? world.StructureSystemId(structure)
            : database.Catalog.FindStation(ship.StationId)!.SolarSystemId;
    }

    public CapsuleerBatch Run(int destinyStamp, int maximumActions = CapsuleerJobQueue.ActionsPerTurn) =>
        Run(destinyStamp, maximumActions, CancellationToken.None);

    public CapsuleerBatch Run(int destinyStamp, int maximumActions, CancellationToken cancellationToken) =>
        RunPrepared(destinyStamp, maximumActions, cancellationToken);

    public CapsuleerBatch RunMaintenance(int destinyStamp, int maximumActions, CancellationToken cancellationToken) =>
        RunPrepared(
            destinyStamp,
            maximumActions: 1,
            cancellationToken,
            backgroundActions: Math.Min(BackgroundActionsPerSlice, maximumActions)
        );

    private CapsuleerBatch RunCore(
        int destinyStamp,
        int maximumActions,
        HumanActivitySnapshot activity,
        CapsuleerHauler.PreparedJourney? preparedJourney = null,
        bool measured = false,
        long? turnStarted = null,
        Predicate<CapsuleerJob>? mayExecute = null
    )
    {
        long started = turnStarted ?? clock.GetTimestamp();
        long throughTicks = activity.ElapsedTicks;
        Dictionary<long, MinerObservation> mining = [];
        Dictionary<long, HaulerObservation> hauling = [];
        CapsuleerJob? executing = null;
        bool actedInObservedSystem = false;
        try
        {
            if (!activity.Active)
            {
                return new(new(0, _registrations.Count, null, false), [], []);
            }
            using var queuePhase = SessionPerformance.Phase("traffic.queueExecution");
            var turn = _queue.Run(
                throughTicks,
                job =>
                {
                    executing = job;
                    long pilot = job.PilotId;
                    int system = _locations[pilot];
                    bool observed = activity.ActiveSystems.Contains(system);
                    actedInObservedSystem |= observed;
                    if (!observed && !CanRunUnobserved(pilot))
                    {
                        throw new InvalidOperationException("The capsuleer job has no active system or travel assignment.");
                    }
                    var live = Live;
                    using var systemContext = live?.UseSystem(system);
                    if (_resuming.TryGetValue(pilot, out long entry))
                    {
                        var saved = Current[pilot];
                        long paused = _paused.GetValueOrDefault(pilot)?.ElapsedTicks ?? 0;
                        var resumed =
                            MinerWorker(pilot)?.Resume(saved, entry, paused, activity.ActiveSystems.Contains(system))
                            ?? HaulerWorker(pilot)!.Resume(saved, entry, paused);
                        Current[pilot] = resumed;
                        _resuming.Remove(pilot);
                        _paused.Remove(pilot);
                        return resumed;
                    }
                    if (Current.GetValueOrDefault(pilot) != job)
                    {
                        throw new InvalidOperationException("A stale capsuleer job cannot execute.");
                    }
                    using var actionPhase = SessionPerformance.IncludesPhases
                        ? SessionPerformance.Phase($"traffic.action.{job.Action}")
                        : null;
                    using var measurement = measured
                        ? null
                        : live?.TrafficActions.Measure(
                            job,
                            [_locations[job.PilotId]],
                            () => (live.CheckpointCopies, live.CheckpointCopying)
                        );
                    CapsuleerJob next;
                    live?.ObserveTrafficJob(job, system);
                    var miner = MinerWorker(pilot);
                    CapsuleerHauler? hauler = null;
                    int destination;
                    using var phase = SessionPerformance.Phase(miner is not null ? "traffic.minerAction" : "traffic.haulerAction");
                    if (miner is not null)
                    {
                        int cycles = miner.CompletedCycles;
                        next = miner.Execute(
                            job,
                            destinyStamp,
                            activity.ElapsedTicks,
                            activity.Resumes,
                            background: !activity.ActiveSystems.Contains(system)
                        );
                        Current[pilot] = next;
                        _completed++;
                        _completedMining++;
                        if (job.Action == CapsuleerAction.Collect && miner.CompletedCycles > cycles)
                        {
                            PaidCollections++;
                        }
                        destination = miner.SystemId;
                    }
                    else if ((hauler = HaulerWorker(pilot)) is not null)
                    {
                        bool wasGang = ActiveOperation(pilot);
                        next = hauler.Execute(
                            job,
                            destinyStamp,
                            background: !activity.ActiveSystems.Contains(system) && !wasGang,
                            preparedJourney: preparedJourney
                        );
                        _membershipDirty |= wasGang != ActiveOperation(pilot);
                        Current[pilot] = next;
                        _completed++;
                        _completedHauling++;
                        destination = hauler.SystemId;
                    }
                    else
                    {
                        throw new InvalidDataException("The capsuleer job has no supported role.");
                    }
                    IndexLocation(pilot, destination);
                    _systems.Register(destination);
                    if (ApplyWorkerChange(live, pilot, destination, restoreCrew: true))
                    {
                        mining.Remove(pilot);
                        hauling.Remove(pilot);
                        miner = MinerWorker(pilot);
                        hauler = HaulerWorker(pilot);
                    }
                    if (miner is not null)
                    {
                        var observation = miner.Read(includeHealth: activity.ActiveSystems.Contains(destination))!;
                        if (
                            observation.Work.OperatingStationId is int baseStation
                            && observation.Pilot.StationId == baseStation
                            && observation.Pilot.Position is null
                        )
                        {
                            RebaseMiningCrew(pilot, miner, observation.Pilot.SystemId);
                        }
                        mining[pilot] = observation;
                        Blocking(pilot, observation.WaitingReason);
                        if (NeedsPresentation(observation.Pilot, system, activity.ActiveSystems))
                        {
                            _pendingMiners[pilot] = observation;
                        }
                        RegisterShip(pilot, observation.Pilot.ShipId);
                    }
                    else if (hauler is not null)
                    {
                        using var observationContext = live?.UseSystem(destination);
                        var observation = hauler.Read(includeHealth: activity.ActiveSystems.Contains(destination))!;
                        hauling[pilot] = observation;
                        Blocking(pilot, observation.Work.WaitingReason);
                        if (NeedsPresentation(observation.Pilot, system, activity.ActiveSystems))
                        {
                            _pendingHaulers[pilot] = observation;
                        }
                        RegisterShip(pilot, observation.Pilot.ShipId);
                    }
                    if (!activity.ActiveSystems.Contains(_locations[pilot]) && !CanRunUnobserved(pilot))
                    {
                        PausePilot(pilot, throughTicks);
                        _running.Remove(pilot);
                    }
                    return next;
                },
                clock,
                _running,
                useCanonicalQueue: true,
                maximumActions: maximumActions,
                check: (job, at) =>
                {
                    executing = job;
                    CheckMiningBelt(job, at, activity);
                },
                firstCheck: job => FirstMiningCheck(job, activity),
                mayExecute: mayExecute
            );
            executing = null;
            // The owner is held throughout this turn. An offscreen action cannot move a
            // human gate watcher. Transfers and changes of assignment mark membership dirty.
            if (actedInObservedSystem || _membershipDirty)
            {
                Reconcile(activity);
            }
            if (turn.OldestDueTicks is null)
            {
                Settle(Math.Max(_settledTicks, throughTicks));
                // Use the capacity freed by this turn before another deadline comes due.
                int starts = StartBackgroundPilots(throughTicks);
                if (starts > 0)
                {
                    turn = turn with
                    {
                        Pending = turn.Pending + starts,
                        OldestDueTicks = _queue.NextDueTicks is { } due && due <= throughTicks ? due : null,
                    };
                }
            }
            return new(turn, mining.Values.ToArray(), hauling.Values.ToArray());
        }
        catch (Exception error)
        {
            RecordFailure(executing, activity.ElapsedTicks, error);
            throw;
        }
        finally
        {
            PublishDiagnostics(activity, started);
        }
    }

    internal long EarlyMiningChecks { get; private set; }
    internal long QuietMiningChecks { get; private set; }
    internal long PaidCollections { get; private set; }

    private long? FirstMiningCheck(CapsuleerJob job, HumanActivitySnapshot activity)
    {
        if (
            job.Action != CapsuleerAction.Collect
            || !_miners.TryGetValue(job.PilotId, out var miner)
            || !miner.WatchesPaidCycle(job, out long started)
            || activity.ActiveSystems.Contains(_locations[job.PilotId])
        )
        {
            return null;
        }
        long at = checked(started + TimeSpan.TicksPerSecond);
        return at < job.DueTicks ? at : null;
    }

    private void CheckMiningBelt(CapsuleerJob job, long at, HumanActivitySnapshot activity)
    {
        EarlyMiningChecks++;
        int system = _locations[job.PilotId];
        using var context = Live?.UseSystem(system);
        if (!activity.ActiveSystems.Contains(system) && _miners.TryGetValue(job.PilotId, out var miner) && miner.BeltIsQuiet(at))
        {
            QuietMiningChecks++;
            _queue.ScheduleCheck(job, checked(at + TimeSpan.TicksPerSecond));
        }
        else
        {
            WakePilot(job.PilotId, at);
        }
    }

    private void RecordFailure(CapsuleerJob? job, long elapsed, Exception error)
    {
        _failures++;
        _lastFailure = job is null ? error.Message : $"Pilot {job.PilotId}, {job.Action}: {error.GetType().Name}: {error.Message}";
        if (job is not null && _locations.TryGetValue(job.PilotId, out int system))
        {
            ApplyWorkerChange(Live, job.PilotId, system, restoreCrew: false);
        }
        // Observation can fail after settlement. Restore canonical revisions before
        // delaying this pilot so committed inventory, payments and travel cannot replay.
        _queue.Restore(_running.Select(ScheduledJob));
        if (job is not null && _running.Contains(job.PilotId))
        {
            _queue.Defer(ScheduledJob(job.PilotId), elapsed);
            Blocking(job.PilotId, _lastFailure);
            ReleaseWorker(job.PilotId);
        }
    }

    private void Reconcile(HumanActivitySnapshot activity)
    {
        var policy = _populationSettings.Settings.Capsuleers;
        IReadOnlyList<WatchedTrafficGate> gates =
            policy.Enabled && policy.GateVisitors > 0 && activity.ActiveSystems.Count > 0 ? gateWatch.Read(activity) : [];
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
        _diagnosticTimestamp = null;
        var live = Live;
        if (activity.Active && policy.Enabled && live is not null && !_backgroundPilotsLoaded)
        {
            if (!_restoredOperations)
            {
                WorldActionScope.Inspect(database, RestoreRoamingGangs);
                _restoredOperations = true;
            }
            ClearBackgroundStarts();
            foreach (var start in live.Economy.CapsuleerStartCandidates())
            {
                if (!_running.Contains(start.Pilot))
                {
                    QueueBackgroundStart(start.Pilot, Current.GetValueOrDefault(start.Pilot)?.DueTicks ?? start.DueTicks);
                }
            }
            _backgroundPilotsLoaded = true;
        }
        foreach (int system in activity.ActiveSystems)
        {
            if (!_visited.Contains(system))
            {
                if (Live is null)
                {
                    using CapsuleerProvisioner provisioner = new(database, clock);
                    provisioner.EnsureSystem(system);
                    Load(system);
                }
                else
                {
                    WorldActionScope.Inspect(database, world => LoadSystem(world, system, null, includeRemote: false));
                }
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
        ReconcileInbound(gates, activity.ActiveSystems);
        var running = _nextRunning;
        running.Clear();
        foreach (int system in activity.ActiveSystems)
        {
            foreach (long pilot in _systemAgents.GetValueOrDefault(system) ?? [])
            {
                if (!_suspended.Contains(pilot))
                {
                    running.Add(pilot);
                    RemoveBackgroundStart(pilot);
                }
            }
        }
        if (activity.Active)
        {
            foreach (long pilot in _registrations.Keys)
            {
                if (!_suspended.Contains(pilot) && CanRunUnobserved(pilot))
                {
                    if (_running.Contains(pilot))
                    {
                        running.Add(pilot);
                    }
                    else if (!running.Contains(pilot))
                    {
                        QueueBackgroundStart(pilot, Current[pilot].DueTicks);
                    }
                }
            }
        }
        foreach (long pilot in _running.Except(running))
        {
            PausePilot(pilot, activity.ElapsedTicks);
            if (activity.Active)
            {
                _queue.Remove(pilot);
            }
        }
        foreach (long pilot in running.Except(_running))
        {
            StartPilot(pilot, activity.ElapsedTicks);
        }
        bool goingIdle = !activity.Active && _reconciledSystems.Length > 0;
        if (live is not null && (goingIdle || _suspended.Count > 0))
        {
            foreach (long pilot in _registrations.Keys.Where(pilot => goingIdle || _suspended.Contains(pilot)).ToArray())
            {
                ReleaseRegistration(live, pilot);
            }
        }
        if (goingIdle)
        {
            ClearBackgroundStarts();
            _waitingDeadlines.TrimExcess();
            _queue.Restore([]);
            _restoredOperations = false;
        }
        _backgroundPilotsLoaded &= activity.Active;
        _nextRunning = _running;
        _running = running;
        _membershipDirty = false;
        WakeObservedMiners(activity.ElapsedTicks, transitions);
        UpdateDetailRetention(activity, transitions);
        _reconciledSystems = activity.ActiveSystems.ToArray();
        _reconciledPilots = activity.Pilots;
        _reconciledResumes = activity.Resumes;
        _reconciledGates = gates.ToArray();
        Volatile.Write(
            ref _diagnostics,
            _diagnostics with
            {
                Jobs = _registrations.Count,
                ScheduledAgents = running.Count,
                WaitingBackgroundStarts = _waitingBackground.Count,
                DueJobs = activity.Active ? _diagnostics.DueJobs : 0,
                OldestDelayTicks = activity.Active ? _diagnostics.OldestDelayTicks : 0,
            }
        );
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
        ReleaseWorker(pilot);
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
            long? previousDue = NextWorkTicks();
            action();
            using var reconciliation = SessionLatency.Measure(SessionLatencyPhase.TrafficReconcile);
            Load();
            var activity = presence.Read();
            Reconcile(activity);
            // Publish newly prepared metadata before observer frames begin.
            Load();
            SettleReadySystems(activity);
            if (NextWorkTicks() != previousDue)
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

    public IReadOnlyList<CapsuleerJob> Jobs()
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            IEnumerable<CapsuleerJob> jobs = Current.Values;
            if (Live is not null && _reconciledResumes > 0)
            {
                // Job inspection can read dormant deadlines without admitting their pilots.
                jobs = jobs.Concat(
                    WorldActionScope.Inspect(database, CapsuleerPopulationStore.Jobs).Where(job => !Current.ContainsKey(job.PilotId))
                );
            }
            return jobs.OrderBy(j => j.DueTicks).ThenBy(j => j.PilotId).ToArray();
        }
    }

    public CapsuleerWakePlan NextWake()
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            var activity = presence.Read();
            return new(
                activity.Active ? CapsuleerScheduleRules.WakeDelay(NextWorkTicks(), activity.ElapsedTicks, true) : null,
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
            return (Live is not null && !_running.Contains(pilotId) ? ReadDormantMiner(pilotId) : MinerWorker(pilotId))?.Read();
        }
    }

    public HaulerObservation? Hauler(long pilotId)
    {
        using var owner = Live?.EnterShared();
        using (synchronization.Enter("CapsuleerScheduler"))
        {
            Load();
            return (Live is not null && !_running.Contains(pilotId) ? ReadDormantHauler(pilotId) : HaulerWorker(pilotId))?.Read();
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

    private void WakePilot(long pilot, long dueTicks)
    {
        if (
            _resuming.ContainsKey(pilot)
            || !Current.TryGetValue(pilot, out var job)
            || job.DueTicks <= dueTicks
            || _waitingDeadlines.ContainsKey(pilot)
        )
        {
            return;
        }
        // An external event revises the pending job without consuming an action or paid work.
        var next = job with
        {
            Revision = checked(job.Revision + 1),
            DueTicks = dueTicks,
        };
        Current[pilot] = next;
        _queue.Publish(next);
        Live?.ObjectsChanged();
    }
}
