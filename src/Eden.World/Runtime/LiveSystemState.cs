using Eden.World.Capsuleers;
using Eden.World.Runtime.Combat;
using Eden.World.Storage;

namespace Eden.World.Runtime;

internal sealed class LiveSystemState
{
    internal System.Collections.Concurrent.ConcurrentQueue<IReadOnlyCollection<CombatRowChange>> PendingChanges { get; } = new();
    internal LiveCombatRuntime? Combat { get; }
    internal LiveReadModels? Reads { get; }
    internal LiveShipHeat Heat { get; }
    internal LiveTractors Tractors { get; }
    internal LiveNaniteRepairs Nanites { get; }
    internal LiveTimedModules TimedModules { get; }
    internal bool Expiring { get; set; }
    internal bool SimulationEntered { get; set; }
    internal HashSet<long> Capsuleers { get; } = [];
    internal Dictionary<long, CapsuleerMiner> Miners { get; } = [];
    internal Dictionary<long, CapsuleerHauler> Haulers { get; } = [];

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
        TimedModules = new(database, system);
    }
}
