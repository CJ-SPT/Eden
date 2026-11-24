using System.Collections.Concurrent;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;

namespace Eden.World.Runtime;

internal sealed class LiveSystemState
{
    internal long PresentationRevision;
    private readonly ConcurrentQueue<IReadOnlyCollection<CombatRowChange>> _pendingChanges = new();
    private IReadOnlyCollection<CombatRowChange>? _applyingChanges;

    internal bool HasPendingChanges => _applyingChanges is not null || !_pendingChanges.IsEmpty;

    internal void DeferChanges(IReadOnlyCollection<CombatRowChange> changes) => _pendingChanges.Enqueue(changes);

    internal void ApplyPendingChanges(Action<LiveSystemState, IReadOnlyCollection<CombatRowChange>> observe)
    {
        // The system owner retries the current batch if applying it fails. Dequeue
        // releases completed batches even when no later changes reach this system.
        while (_applyingChanges is not null || _pendingChanges.TryDequeue(out _applyingChanges))
        {
            observe(this, _applyingChanges);
            _applyingChanges = null;
        }
    }

    internal LiveCombatRuntime? Combat { get; }
    internal LiveReadModels? Reads { get; }
    internal LiveShipHeat Heat { get; }
    internal LiveTractors Tractors { get; }
    internal LiveNaniteRepairs Nanites { get; }
    internal LiveTimedModules TimedModules { get; }
    internal bool Expiring { get; set; }
    internal bool SimulationEntered { get; set; }

    internal LiveSystemState(
        LiveWorld owner,
        WorldDatabase database,
        TimeProvider clock,
        bool combat,
        bool reads,
        bool simulation,
        int? system = null
    )
    {
        Combat = combat ? new(owner, database, simulation, system) : null;
        Reads = reads ? new(owner, database) : null;
        Heat = new(owner, database, clock);
        Tractors = new(owner, database);
        Nanites = new(database, clock, system);
        TimedModules = new(owner, system);
    }
}
