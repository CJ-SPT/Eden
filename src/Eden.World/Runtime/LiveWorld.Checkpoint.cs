using System.Collections.Concurrent;
using System.Diagnostics;
using Eden.World.Capsuleers;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;

namespace Eden.World.Runtime;

internal sealed record WorldPersistenceWait(string Operation, int? SystemId, TimeSpan Delay, bool DuringCheckpointCopy);

internal sealed record WorldResidentCapture(
    long Generation,
    CombatCheckpoint? Combat,
    ShipHeatCheckpoint Heat,
    TractorCheckpoint Tractors,
    IReadOnlyList<MinerCheckpoint> Miners,
    IReadOnlyList<HaulerCheckpoint> Haulers,
    CapsuleerClockCheckpoint? Activity,
    CapsuleerJobCheckpoint Jobs,
    CapsuleerRoutineCheckpoint Routines,
    TimeSpan Wait,
    TimeSpan Capture,
    long AllocatedBytes
);

internal sealed partial class LiveWorld
{
    private bool _checkpointCopying;
    private const int RetainedPersistenceWaits = 128;
    private readonly ConcurrentQueue<WorldPersistenceWait> _persistenceWaits = new();
    internal IReadOnlyList<WorldPersistenceWait> PersistenceWaits => _persistenceWaits.ToArray();

    private void RecordPersistenceWait(string operation, TimeSpan delay)
    {
        _persistenceWaits.Enqueue(new(operation, CurrentSystem, delay, true));
        while (_persistenceWaits.Count > RetainedPersistenceWaits)
        {
            _persistenceWaits.TryDequeue(out _);
        }
    }

    internal IDisposable EnterSql(TransactionParticipants? participants)
    {
        if (_ownershipMonitor.IsEntered || _heldSystems.Value!.Count != 0)
        {
            return EnterPersistence("SQLite connection", participants);
        }
        IDisposable lease;
        using (Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("persistence.writerWait"))
        {
            lease = EnterPersistence("SQLite connection", participants);
        }
        return lease;
    }

    private WorldResidentCapture? CaptureResidents()
    {
        long started = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        TimeSpan wait = TimeSpan.Zero;
        long admissionStarted = Stopwatch.GetTimestamp();
        // Freeze only admission, not existing resident operations. New systems may
        // begin again before the relational copy, and belong to the next generation.
        using var admission = _systems.FreezeAdmissions();
        wait += Stopwatch.GetElapsedTime(admissionStarted);
        while (true)
        {
            var entries = _systems.Snapshot();
            long ownersStarted = Stopwatch.GetTimestamp();
            using var owners = _systems.Enter(entries.Select(e => e.Id), "checkpoint resident capture");
            wait += Stopwatch.GetElapsedTime(ownersStarted);
            Heat.Advance(SimulationTime);
            Nanites.Advance(SimulationTime);
            foreach (var entry in entries.Where(e => e.State.SimulationEntered))
            {
                using var context = UseSystem(entry.Id);
                using var ownership = EnterSimulation("checkpoint deadlines", entry.Id);
            }
            if (!_systems.Snapshot().Select(e => e.Id).SequenceEqual(entries.Select(e => e.Id)))
            {
                // Deadline settlement may admit a runtime on this coordinator thread.
                // Release the complete group and reacquire the expanded sorted set.
                continue;
            }
            var activity = CapsuleerActivity.Capture();
            if (activity is not null && activity.ElapsedTicks != _activityCheckpointTicks)
            {
                _activityCheckpointTicks = activity.ElapsedTicks;
                ObjectsChanged();
            }
            long generation = ObserveGeneration();
            if (generation == _savedGeneration)
            {
                return null;
            }
            var combat = _shared.Combat?.CaptureCheckpoint();
            var heat = new ShipHeatCheckpoint(AllStates.SelectMany(state => state.Heat.CaptureOwn()?.Ships ?? []).ToArray());
            var tractors = new TractorCheckpoint(AllStates.SelectMany(state => state.Tractors.Capture()?.Positions ?? []).ToArray());
            return new(
                generation,
                combat,
                heat,
                tractors,
                MinerCheckpoints.Values.Where(m => ProjectedMiners.GetValueOrDefault(m.PilotId) != m).ToArray(),
                HaulerCheckpoints.Values.Where(h => ProjectedHaulers.GetValueOrDefault(h.PilotId) != h).ToArray(),
                activity,
                new(CapsuleerJobs.Values.Where(j => ProjectedCapsuleerJobs.GetValueOrDefault(j.PilotId) != j).ToArray()),
                CapsuleerRoutines.Capture(),
                wait,
                Stopwatch.GetElapsedTime(started) - wait,
                GC.GetAllocatedBytesForCurrentThread() - allocated
            );
        }
    }
}
