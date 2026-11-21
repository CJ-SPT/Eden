using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private void RefreshChangedPopulation(WorldAction world, LiveWorld live)
    {
        long previous = _populationRevision;
        var occupied = presence.Read().ActiveSystems;
        var pilots = live.CapsuleerAgentRevisions.Where(change => change.Value > previous).Select(change => change.Key).ToArray();
        if (pilots.Length > 0)
        {
            _suspended = world.Set<SavedCapsuleerSuspension>().Select(s => s.PilotId).ToHashSet();
            _membershipDirty = true;
        }
        foreach (int system in occupied)
        {
            if (live.CapsuleerSystemRevisions.GetValueOrDefault(system) > previous)
            {
                LoadSystem(world, system, null, includeRemote: false);
                _membershipDirty = true;
            }
        }
        foreach (long pilot in pilots)
        {
            RemoveBackgroundStart(pilot);
            if (_suspended.Contains(pilot) || world.Set<SavedCapsuleerAgent>().Find(pilot) is not { } agent)
            {
                continue;
            }
            if (
                _registrations.ContainsKey(pilot)
                || occupied.Count > 0 && occupied.Contains(PilotSystem(world, world.Pilots.Find(pilot)!.ActiveShipId))
            )
            {
                Register(world, [agent]);
            }
            else if (_backgroundPilotsLoaded && SupportsBackground(agent.Career) && world.Set<SavedCapsuleerJob>().Find(pilot) is { } job)
            {
                QueueBackgroundStart(pilot, Current.GetValueOrDefault(pilot)?.DueTicks ?? job.DueTicks);
            }
        }
    }
}
