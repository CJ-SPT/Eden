using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

// Shared scheduler ownership serializes cursor publication with economic commits and capture.
internal sealed class LiveCapsuleerRoutines(LiveWorld owner, string path)
{
    private readonly Dictionary<int, CapsuleerCoarseWork> _work = [];
    private readonly Dictionary<long, long> _settled = [];
    internal IReadOnlyDictionary<int, CapsuleerCoarseWork> Work => _work;
    internal IReadOnlyDictionary<long, long> Settled => _settled;
    private readonly Dictionary<int, CapsuleerActivation> _activations = [];
    private readonly HashSet<long> _admissions = [];
    private readonly HashSet<int> _dirtyWork = [];
    private readonly HashSet<long> _dirtySettled = [];
    private bool _projecting;
    internal long Revision { get; private set; }

    internal CapsuleerRoutineCheckpoint Capture(TransactionParticipants? participants = null, IReadOnlyCollection<long>? pilots = null) =>
        new(
            (participants is null ? _dirtyWork : participants.Systems.Where(_dirtyWork.Contains)).Select(id => _work[id]).ToArray(),
            (pilots is null ? _dirtySettled : pilots.Where(_dirtySettled.Contains))
                .Select(id => new CapsuleerSettlement(id, _settled[id]))
                .ToArray(),
            (
                participants is null
                    ? _activations.Values
                    : participants.Systems.Where(_activations.ContainsKey).Select(id => _activations[id])
            ).ToArray(),
            (pilots is null ? _admissions : pilots.Where(_admissions.Contains)).ToArray()
        );

    internal void Activate(int system, long elapsed)
    {
        _activations.TryAdd(system, new(system, elapsed));
        owner.ObjectsChanged();
    }

    internal void Admit(long pilot)
    {
        _admissions.Add(pilot);
        owner.ObjectsChanged();
    }

    internal void Restore(CapsuleerCoarseWork work)
    {
        _work[work.SystemId] = work;
        _dirtyWork.Remove(work.SystemId);
    }

    internal void Restore(long pilot, long elapsed)
    {
        _settled[pilot] = elapsed;
        _dirtySettled.Remove(pilot);
    }

    internal void Publish(CapsuleerCoarseWork work)
    {
        _work[work.SystemId] = work;
        _dirtyWork.Add(work.SystemId);
        owner.ObjectsChanged();
    }

    internal void Settle(long pilot, long elapsed)
    {
        _settled[pilot] = elapsed;
        _dirtySettled.Add(pilot);
        owner.ObjectsChanged();
    }

    internal void Validate(CapsuleerCoarseWork expected)
    {
        if (
            !_work.TryGetValue(expected.SystemId, out var current)
            || current.Generation != expected.Generation
            || current.Cursor != expected.Cursor
        )
        {
            throw new CapsuleerCoarseConflictException(expected.SystemId);
        }
    }

    internal void Complete(CapsuleerCoarseWork expected, long pilot)
    {
        Publish(expected with { Cursor = pilot, Generation = checked(expected.Generation + 1) });
        Settle(pilot, expected.ThroughTicks);
    }

    internal void Flush(TransactionParticipants? participants = null)
    {
        if (_projecting)
        {
            return;
        }
        using var ownership = owner.EnterTransactionSystems(participants);
        var pilots = owner.CapsuleerParticipants(participants);
        var capture = Capture(participants, pilots);
        var jobs = new CapsuleerJobCheckpoint(
            pilots
                .Select(id => owner.CapsuleerJobs.GetValueOrDefault(id))
                .OfType<CapsuleerJob>()
                .Where(j => owner.ProjectedCapsuleerJobs.GetValueOrDefault(j.PilotId) != j)
                .ToArray()
        );
        var miners = pilots
            .Select(id => owner.MinerCheckpoints.GetValueOrDefault(id))
            .OfType<MinerCheckpoint>()
            .Where(m => owner.ProjectedMiners.GetValueOrDefault(m.PilotId) != m)
            .ToArray();
        var haulers = pilots
            .Select(id => owner.HaulerCheckpoints.GetValueOrDefault(id))
            .OfType<HaulerCheckpoint>()
            .Where(h => owner.ProjectedHaulers.GetValueOrDefault(h.PilotId) != h)
            .ToArray();
        if (
            capture.Work.Count
                + capture.Settled.Count
                + capture.Activations.Count
                + capture.Admissions.Count
                + jobs.Jobs.Count
                + miners.Length
                + haulers.Length
            == 0
        )
        {
            return;
        }
        _projecting = true;
        try
        {
            using WorldDbContext world = new(path) { IsResidentProjection = true };
            using var transaction = world.Database.BeginTransaction();
            capture.Apply(world);
            jobs.Apply(world);
            MinerCheckpoint.Apply(world, miners);
            HaulerCheckpoint.Apply(world, haulers);
            transaction.Commit();
            foreach (var activation in capture.Activations)
            {
                _activations.Remove(activation.SystemId);
            }
            _admissions.ExceptWith(capture.Admissions);
            foreach (var job in jobs.Jobs)
            {
                owner.ProjectedCapsuleerJobs[job.PilotId] = job;
            }
            foreach (var miner in miners)
            {
                owner.ProjectedMiners[miner.PilotId] = miner;
            }
            foreach (var hauler in haulers)
            {
                owner.ProjectedHaulers[hauler.PilotId] = hauler;
            }
            foreach (var work in capture.Work)
            {
                _dirtyWork.Remove(work.SystemId);
            }
            foreach (var pilot in capture.Settled)
            {
                _dirtySettled.Remove(pilot.PilotId);
            }
        }
        finally
        {
            _projecting = false;
        }
    }

    internal void Observe(IReadOnlyCollection<CombatRowChange> changes)
    {
        if (_projecting)
        {
            return;
        }
        foreach (var change in changes)
        {
            if (change.Table == "WorldCapsuleerCoarseWork")
            {
                Revision++;
                using WorldDbContext world = new(path) { IsResidentProjection = true };
                var row = world.Set<SavedCapsuleerCoarseWork>().AsNoTracking().SingleOrDefault(w => w.SystemId == change.Id);
                if (row is null)
                {
                    _work.Remove(checked((int)change.Id));
                    _dirtyWork.Remove(checked((int)change.Id));
                }
                else
                {
                    Restore(new(row.SystemId, row.Generation, row.Cursor, row.SettledTicks, row.NextTicks));
                }
            }
            else if (change.Table == "WorldCapsuleerCoarsePilot")
            {
                using WorldDbContext world = new(path) { IsResidentProjection = true };
                var row = world.Set<SavedCapsuleerCoarsePilot>().AsNoTracking().SingleOrDefault(p => p.PilotId == change.Id);
                if (row is null)
                {
                    _settled.Remove(change.Id);
                    _dirtySettled.Remove(change.Id);
                }
                else
                {
                    Restore(row.PilotId, row.SettledTicks);
                }
            }
        }
    }
}
