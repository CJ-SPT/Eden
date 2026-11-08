using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerScheduler(
    WorldDatabase database,
    CapsuleerActivitySource presence,
    IWorldSynchronization synchronization,
    TimeProvider clock,
    CapsuleerMinerFactory miners,
    CapsuleerHaulerFactory haulers
) : ICapsuleerScheduler, ICapsuleerSimulation, ICapsuleerHauling, ICapsuleerHandoff
{
    private readonly SystemSimulationRegistry _systems = new();
    private readonly Dictionary<long, MinerObservation> _pendingMiners = [];
    private readonly Dictionary<long, HaulerObservation> _pendingHaulers = [];
    private readonly HashSet<int> _activated = [];
    private long _presentationRevision;
    private long _settledTicks;
    private int _destinyStamp;
    private readonly CapsuleerJobQueue _queue = new();
    private readonly Dictionary<long, CapsuleerJob> _jobs = [];
    private readonly Dictionary<long, CapsuleerMiner> _miners = [];
    private readonly Dictionary<long, CapsuleerHauler> _haulers = [];
    private readonly Dictionary<long, long> _ships = [];
    private readonly Dictionary<long, long> _pilotShips = [];
    private readonly Dictionary<long, int> _locations = [];

    private sealed record SavedLocation(long PilotId, int StationId, int? SystemId);

    private bool _loaded;
    private long _populationRevision;
    private LiveWorld? Live => LiveWorld.Find(database.Path);
    private Dictionary<long, CapsuleerJob> Current => Live?.CapsuleerJobs ?? _jobs;

    private void Load()
    {
        if (_loaded && _populationRevision == (Live?.CapsuleerRevision ?? 0))
        {
            return;
        }
        using WorldDbContext world = new(database.Path);
        var agents = world.Set<SavedCapsuleerAgent>().AsNoTracking().OrderBy(a => a.PilotId).ToArray();
        var jobs = CapsuleerPopulationStore.Jobs(world);
        var locations = (
            from agent in world.Set<SavedCapsuleerAgent>()
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
        foreach (var location in locations)
        {
            _locations[location.PilotId] = location.SystemId ?? systems[location.StationId];
        }
        foreach (var job in jobs)
        {
            Current.TryAdd(job.PilotId, job);
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
        _queue.Restore(Current.Values);
        _populationRevision = Live?.CapsuleerRevision ?? 0;
        _loaded = true;
    }

    public CapsuleerBatch Run(int destinyStamp)
    {
        using var owner = Live?.Enter();
        lock (synchronization.Gate)
        {
            _destinyStamp = destinyStamp;
            var activity = presence.Read();
            Reconcile(activity);
            Load();
            Dictionary<long, MinerObservation> mining = [];
            Dictionary<long, HaulerObservation> hauling = [];
            CapsuleerTurn turn;
            try
            {
                turn = _queue.Run(
                    activity.ElapsedTicks,
                    job =>
                    {
                        if (Current.GetValueOrDefault(job.PilotId) != job)
                        {
                            throw new InvalidOperationException("A stale capsuleer job cannot execute.");
                        }
                        CapsuleerJob next;
                        if (_miners.TryGetValue(job.PilotId, out var miner))
                        {
                            bool background = !activity.ActiveSystems.Contains(miner.SystemId);
                            next = miner.Execute(job, destinyStamp, activity.ElapsedTicks, activity.Resumes, background);
                            Current[job.PilotId] = next;
                            var observation = miner.Read()!;
                            mining[job.PilotId] = observation;
                            _pendingMiners[job.PilotId] = observation;
                            _locations[job.PilotId] = observation.Pilot.SystemId;
                            _systems.Register(observation.Pilot.SystemId);
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else if (_haulers.TryGetValue(job.PilotId, out var hauler))
                        {
                            bool background = !activity.ActiveSystems.Contains(hauler.SystemId);
                            next = hauler.Execute(job, destinyStamp, background);
                            Current[job.PilotId] = next;
                            var observation = hauler.Read()!;
                            hauling[job.PilotId] = observation;
                            _pendingHaulers[job.PilotId] = observation;
                            _locations[job.PilotId] = observation.Pilot.SystemId;
                            _systems.Register(observation.Pilot.SystemId);
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else
                        {
                            throw new InvalidDataException("The capsuleer job has no supported role.");
                        }
                        Current[job.PilotId] = next;
                        return next;
                    },
                    clock
                );
            }
            catch
            {
                // A committed action can precede a failed observation or another role.
                // Rebuild from canonical revisions so retry cannot replay that action.
                _queue.Restore(Current.Values);
                throw;
            }
            if (turn.OldestDueTicks is null)
            {
                Settle(activity.ElapsedTicks);
            }
            return new(turn, mining.Values.ToArray(), hauling.Values.ToArray());
        }
    }

    private void Reconcile(HumanActivitySnapshot activity)
    {
        foreach (var state in _systems.Begin(activity.ActiveSystems))
        {
            if (state.Mode == SystemSimulationMode.Deactivating)
            {
                _activated.Remove(state.SystemId);
                Live?.Combat?.ReleaseObserverContacts(state.SystemId);
            }
            if (state.Mode == SystemSimulationMode.Activating)
            {
                _activated.Add(state.SystemId);
            }
        }
    }

    private void Settle(long throughTicks)
    {
        _settledTicks = throughTicks;
        foreach (var state in _systems.Read())
        {
            _systems.Settle(state.SystemId, throughTicks);
        }
    }

    public IReadOnlyList<SystemSimulationState> Systems()
    {
        lock (synchronization.Gate)
        {
            return _systems.Read();
        }
    }

    // Release ownership between bounded turns. The action and the final due check
    // share ownership; no timer can insert an older action between them.
    public void Synchronize(Action action)
    {
        while (!TrySynchronize(action))
        {
            Thread.Yield();
        }
    }

    public async ValueTask SynchronizeAsync(Action action, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TrySynchronize(action))
            {
                return;
            }
            await Task.Yield();
        }
    }

    private bool TrySynchronize(Action action)
    {
        lock (synchronization.Gate)
        {
            var turn = Run(_destinyStamp).Turn;
            if (turn.OldestDueTicks is not null)
            {
                return false;
            }
            action();
            Reconcile(presence.Read());
            Settle(_settledTicks);
            return true;
        }
    }

    public CapsuleerBatch Capture()
    {
        lock (synchronization.Gate)
        {
            Reconcile(presence.Read());
            Load();
            var active = _systems.Read().Where(s => s.Mode == SystemSimulationMode.Active).Select(s => s.SystemId).ToHashSet();
            foreach (var location in _locations.Where(p => active.Contains(p.Value)))
            {
                if (
                    _miners.TryGetValue(location.Key, out var miner) && (miner.Sample(_settledTicks) || _activated.Contains(location.Value))
                )
                {
                    var observation = miner.Read()!;
                    _pendingMiners[observation.Pilot.PilotId] = observation;
                }
                if (
                    _haulers.TryGetValue(location.Key, out var hauler)
                    && (hauler.Sample(_settledTicks) || _activated.Contains(location.Value))
                )
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
                Revision = checked(++_presentationRevision),
                Systems = _systems.Read(),
            };
            _pendingMiners.Clear();
            _pendingHaulers.Clear();
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
            }
        }
    }
}
