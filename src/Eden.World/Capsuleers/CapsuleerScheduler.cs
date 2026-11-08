using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerScheduler(
    WorldDatabase database,
    IHumanPresence presence,
    TimeProvider clock,
    CapsuleerMinerFactory miners,
    CapsuleerHaulerFactory haulers
) : ICapsuleerScheduler, ICapsuleerSimulation, ICapsuleerHauling
{
    private readonly Lock _gate = new();
    private readonly CapsuleerJobQueue _queue = new();
    private readonly Dictionary<long, CapsuleerJob> _jobs = [];
    private readonly Dictionary<long, CapsuleerMiner> _miners = [];
    private readonly Dictionary<long, CapsuleerHauler> _haulers = [];
    private readonly Dictionary<long, long> _ships = [];
    private readonly Dictionary<long, long> _pilotShips = [];
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
        lock (_gate)
        {
            var activity = presence.Read();
            if (!activity.Active)
            {
                return new(new(0, Current.Count, null, false), [], []);
            }
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
                            next = miner.Execute(job, destinyStamp, activity.ElapsedTicks, activity.Resumes);
                            Current[job.PilotId] = next;
                            var observation = miner.Read()!;
                            mining[job.PilotId] = observation;
                            RegisterShip(job.PilotId, observation.Pilot.ShipId);
                        }
                        else if (_haulers.TryGetValue(job.PilotId, out var hauler))
                        {
                            next = hauler.Execute(job, destinyStamp);
                            Current[job.PilotId] = next;
                            var observation = hauler.Read()!;
                            hauling[job.PilotId] = observation;
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
            return new(turn, mining.Values.ToArray(), hauling.Values.ToArray());
        }
    }

    public IReadOnlyList<CapsuleerJob> Jobs()
    {
        using var owner = Live?.Enter();
        lock (_gate)
        {
            Load();
            return Current.Values.OrderBy(j => j.DueTicks).ThenBy(j => j.PilotId).ToArray();
        }
    }

    public MinerObservation? Miner(long pilotId)
    {
        using var owner = Live?.Enter();
        lock (_gate)
        {
            Load();
            return _miners.GetValueOrDefault(pilotId)?.Read();
        }
    }

    public HaulerObservation? Hauler(long pilotId)
    {
        using var owner = Live?.Enter();
        lock (_gate)
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
        lock (_gate)
        {
            if (_ships.TryGetValue(shipId, out long pilot))
            {
                _miners.GetValueOrDefault(pilot)?.ObserveAggression(shipId);
                _haulers.GetValueOrDefault(pilot)?.ObserveAggression(shipId);
            }
        }
    }
}
