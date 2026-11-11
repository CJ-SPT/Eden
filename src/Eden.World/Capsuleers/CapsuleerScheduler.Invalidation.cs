using System.Text.Json;
using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private void RestoreDetailedMiners(WorldAction world, IReadOnlyList<SavedCapsuleerAgent> agents)
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
                .Where(c => c.PilotId != null && Enumerable.Contains(page, c.PilotId.Value))
                .ToArray();
            foreach (var saved in pinned)
            {
                if (!HasDefense(saved.Progress))
                {
                    continue;
                }
                long pilot = saved.PilotId!.Value;
                var checkpoint = new MinerCheckpoint(pilot, MinerCheckpoint.Read(saved.Progress)!);
                live.MinerCheckpoints.TryAdd(pilot, checkpoint);
                live.ProjectedMiners.TryAdd(pilot, checkpoint);
            }
        }
    }

    private static bool HasDefense(string? progress)
    {
        if (progress is null)
        {
            return false;
        }
        try
        {
            using var document = JsonDocument.Parse(progress);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("Defense", out var defense)
                && defense.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
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
            WorldActionScope.Inspect(
                database,
                world =>
                {
                    foreach (var batch in pilots.Chunk(CapsuleerCoarseRules.PageSize))
                    {
                        var agents = world
                            .Set<SavedCapsuleerAgent>()
                            .AsNoTracking()
                            .Where(a => Enumerable.Contains(batch, a.PilotId))
                            .ToArray();
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
            );
        }
        _populationRevision = live.CapsuleerRevision;
        _population = population.Summary();
    }
}
