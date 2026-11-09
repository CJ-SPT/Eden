using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcPopulationSchedule
{
    internal static long Next(WorldDbContext world, int system, long now)
    {
        var encounters = world.Set<WorldNpcEncounter>().AsNoTracking().Where(e => e.SystemId == system).ToArray();
        // Guards respawn per slot. Legacy fixture pirates have no encounter row.
        var tracked = encounters.Select(e => e.Id).ToHashSet();
        // Authored anomaly and mission losses advance their own objectives. They never
        // use regional slot respawns, even without a regional encounter row.
        var rows = world
            .Set<WorldNpc>()
            .AsNoTracking()
            .Where(n => n.SystemId == system && n.AnchorKind != NpcAnchorKind.Anomaly && n.AnchorKind != NpcAnchorKind.Mission)
            .ToArray();
        long due = long.MaxValue;
        foreach (var encounter in encounters.Where(e => e.Occupied && e.NextSpawnAt != null))
        {
            long next = encounter.NextSpawnAt!.Value;
            if (encounter.Role == NpcRole.MiningMiner)
            {
                next = CalmMiningDeadline(world, encounter, rows, now) ?? next;
            }
            due = Math.Min(due, next);
        }
        foreach (
            var latest in rows.Where(n => n.Role is NpcRole.Navy or NpcRole.Customs || !tracked.Contains(n.EncounterId))
                .GroupBy(n => (n.EncounterId, n.BeltId, n.SpawnSlot))
                .Select(g => g.MaxBy(n => n.Id)!)
        )
        {
            if (latest.DestroyedAt is { } destroyed && latest.RetiredAt is null)
            {
                due = Math.Min(due, checked(destroyed + NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond));
            }
        }
        // Unsupported catalog pools can remain due; retry at most once per second.
        return due <= now ? checked(now + TimeSpan.TicksPerSecond) : due;
    }

    private static long? CalmMiningDeadline(WorldDbContext world, WorldNpcEncounter encounter, WorldNpc[] rows, long now)
    {
        var operation = world.Set<WorldNpcMiningOperation>().AsNoTracking().SingleOrDefault(o => o.EncounterId == encounter.Id);
        if (
            operation is not { Stage: NpcMiningStage.Working }
            || operation.PickupAt <= now
            || world.Set<SavedNpcStanding>().Any(s => s.SourceId == operation.CorporationId && s.Value <= NpcMiningRules.HostileStanding)
        )
        {
            return null;
        }
        var active = rows.Where(n => n.EncounterId == encounter.Id && n.DestroyedAt == null && n.RetiredAt == null).ToArray();
        if (
            active.Length != NpcMiningRules.MinerCount
            || active.Any(n => n.Role != NpcRole.MiningMiner || n.Navigation != NpcNavigationState.Idle)
        )
        {
            return null;
        }
        var members = world.Set<WorldNpcMiningMember>().AsNoTracking().Where(m => m.EncounterId == encounter.Id).ToArray();
        var current = members.Where(m => active.Any(n => n.Id == m.NpcId)).ToArray();
        if (current.Length != active.Length || current.Any(m => m.MiningDueAt is null || m.MiningDueAt <= now || m.WarpStartedAt != null))
        {
            return null;
        }
        // Unchanged funded beams have no population work before collection/pickup.
        // Ore, standings and NPC mutations invalidate this cached deadline.
        return Math.Min(operation.PickupAt, current.Min(m => m.MiningDueAt!.Value));
    }
}
