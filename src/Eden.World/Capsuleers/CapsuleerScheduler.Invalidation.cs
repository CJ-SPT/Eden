using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private void RestoreDetailedMiners(WorldDbContext world, IReadOnlyList<SavedCapsuleerAgent> agents)
    {
        if (Live is not { } live)
        {
            return;
        }
        var pilots = agents
            .Where(a => a.Career == CapsuleerCareer.Mining && !live.MinerCheckpoints.ContainsKey(a.PilotId))
            .Select(a => a.PilotId);
        foreach (var page in pilots.Chunk(CapsuleerCoarseRules.PageSize))
        {
            var pinned = world
                .Set<SavedCapsuleerCommunity>()
                .FromSqlRaw(
                    """
                    SELECT * FROM WorldCapsuleerCommunity
                    WHERE json_valid(Progress) AND json_type(Progress,'$.Defense')='object'
                    """
                )
                .AsNoTracking()
                .Where(c => c.PilotId != null && page.Contains(c.PilotId.Value))
                .ToArray();
            foreach (var saved in pinned)
            {
                long pilot = saved.PilotId!.Value;
                var checkpoint = new MinerCheckpoint(pilot, MinerCheckpoint.Read(saved.Progress)!);
                live.MinerCheckpoints.TryAdd(pilot, checkpoint);
                live.ProjectedMiners.TryAdd(pilot, checkpoint);
            }
        }
    }

    private void RefreshChangedPopulation(LiveWorld live)
    {
        long previous = _populationRevision;
        var systems = live
            .CapsuleerSystemRevisions.Where(p => p.Value > previous && _loadedSystems.Contains(p.Key))
            .Select(p => p.Key)
            .ToArray();
        var pilots = live.CapsuleerAgentRevisions.Where(p => p.Value > previous).Select(p => p.Key).ToArray();
        // Population preparation also commits metadata for untouched communities.
        // Those commits must never reload every known route or build a giant IN query.
        foreach (int system in systems)
        {
            Load(system);
        }
        if (pilots.Length > 0)
        {
            using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
            foreach (var batch in pilots.Chunk(CapsuleerCoarseRules.PageSize))
            {
                var agents = world.Set<SavedCapsuleerAgent>().AsNoTracking().Where(a => batch.Contains(a.PilotId)).ToArray();
                foreach (var agent in agents)
                {
                    if (_loadedSystems.Contains(agent.CommunitySystemId) || _loadedPilots.Contains(agent.PilotId))
                    {
                        Load(agent.CommunitySystemId, agent.PilotId);
                    }
                }
            }
            _suspended = world.Set<SavedCapsuleerSuspension>().Select(s => s.PilotId).ToHashSet();
            _membershipDirty = true;
        }
        _populationRevision = live.CapsuleerRevision;
        _population = population.Summary();
    }
}
