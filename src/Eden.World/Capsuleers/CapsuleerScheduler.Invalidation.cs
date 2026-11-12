using Eden.Data.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private void RefreshChangedPopulation(LiveWorld live)
    {
        long previous = _populationRevision;
        var occupied = presence.Read().ActiveSystems;
        foreach (int system in occupied)
        {
            if (live.CapsuleerSystemRevisions.GetValueOrDefault(system) > previous)
            {
                Load(system);
            }
        }
        var pilots = live
            .CapsuleerAgentRevisions.Where(change => change.Value > previous && _locations.ContainsKey(change.Key))
            .Select(change => change.Key)
            .ToArray();
        if (pilots.Length > 0)
        {
            WorldActionScope.Inspect(
                database,
                world =>
                {
                    Register(world, world.Set<SavedCapsuleerAgent>().Where(agent => Enumerable.Contains(pilots, agent.PilotId)).ToArray());
                    _suspended = world.Set<SavedCapsuleerSuspension>().Select(s => s.PilotId).ToHashSet();
                }
            );
            _membershipDirty = true;
        }
        _populationRevision = live.CapsuleerRevision;
        _population = population.Summary();
    }
}
