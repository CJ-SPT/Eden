using System.Collections.Frozen;

namespace Eden.World.Storage;

internal enum SharedSimulationResource
{
    Npc,
    Wreck,
}

internal readonly record struct SimulationResource(SharedSimulationResource Kind, long Id);

// Null participants means an unclassified operation and deliberately projects everything.
// A classified transfer must include both endpoints and both owners. Empty means that
// the operation cannot read or mutate existing resident simulation state (e.g. admission).
internal sealed class TransactionParticipants(
    IEnumerable<int>? systems = null,
    IEnumerable<long>? pilots = null,
    IEnumerable<long>? ships = null,
    IEnumerable<SimulationResource>? resources = null
)
{
    internal static readonly TransactionParticipants None = new();
    internal FrozenSet<int> Systems { get; } = (systems ?? []).ToFrozenSet();
    internal FrozenSet<long> Pilots { get; } = (pilots ?? []).ToFrozenSet();
    internal FrozenSet<long> Ships { get; } = (ships ?? []).ToFrozenSet();
    internal FrozenSet<SimulationResource> Resources { get; } = (resources ?? []).ToFrozenSet();

    internal bool Includes(long pilot, long ship, int? system) =>
        Pilots.Contains(pilot) || Ships.Contains(ship) || system is int id && Systems.Contains(id);

    internal bool Includes(SharedSimulationResource kind, long id) => Resources.Contains(new(kind, id));
}
