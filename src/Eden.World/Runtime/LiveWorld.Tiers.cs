using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Runtime;

internal sealed partial class LiveWorld
{
    private IReadOnlySet<int> _detailedSystems = new HashSet<int>();

    internal bool IsDetailedSystem(int system) => Volatile.Read(ref _detailedSystems).Contains(system);

    internal long SimulationTime => _clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();

    internal void PublishDetailedSystems(IEnumerable<int> systems) => Volatile.Write(ref _detailedSystems, systems.ToHashSet());

    internal bool ReleaseSystemDetails(int system)
    {
        using var persistence = EnterPersistence("system demotion");
        var entry = _systems.Get(system);
        using var context = UseSystem(system);
        using var ownership = EnterSimulation("system demotion", system);
        if (entry.State.Combat?.HasDetailedInteraction(_clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()) == true)
        {
            return false;
        }
        bool LoadMissiles()
        {
            using WorldDbContext world = new(_path) { IsResidentProjection = true };
            return world.Set<WorldMissile>().Any(m => m.SystemId == system && m.EndedAt == null);
        }
        if (_shared.Reads?.UnfinishedMissiles(system, LoadMissiles) ?? LoadMissiles())
        {
            return false;
        }
        // Projection is a transaction boundary. A failed projection leaves the dirty
        // records resident and prevents eviction; the next handoff can retry it.
        entry.State.Combat?.Clear();
        entry.State.Reads?.Clear();
        return true;
    }
}
