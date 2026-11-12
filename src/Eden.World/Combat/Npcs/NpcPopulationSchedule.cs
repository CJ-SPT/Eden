using Eden.Data.World.Characters;
using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Mining;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcPopulationSchedule
{
    internal static long Next(WorldDbContext world, int system, long now) => Next(new SqlWorldAction(world), system, now);

    internal static long Next(WorldAction world, int system, long now)
    {
        var encounters = world.Filter<WorldNpcEncounter>(e => e.SystemId == system).ToArray();
        // Guards respawn per slot. Legacy fixture pirates have no encounter row.
        var tracked = encounters.Select(e => e.Id).ToHashSet();
        // Authored anomaly and mission losses advance their own objectives. They never
        // use regional slot respawns, even without a regional encounter row.
        var slots = world
            .Filter<WorldNpc>(n =>
                n.SystemId == system
                && n.AnchorKind != NpcAnchorKind.Anomaly
                && n.AnchorKind != NpcAnchorKind.Mission
                && (n.Role == NpcRole.Navy || n.Role == NpcRole.Customs || !Enumerable.Contains(tracked, n.EncounterId))
            )
            .Select(n => new PopulationSlot(n.Id, n.EncounterId, n.BeltId, n.SpawnSlot, n.DestroyedAt, n.RetiredAt))
            .ToArray();
        long due = long.MaxValue;
        foreach (var encounter in encounters.Where(e => e.Occupied && e.NextSpawnAt != null))
        {
            long next = encounter.NextSpawnAt!.Value;
            if (encounter.Role == NpcRole.MiningMiner)
            {
                next = CalmMiningDeadline(world, encounter, now) ?? next;
            }
            due = Math.Min(due, next);
        }
        foreach (var latest in slots.GroupBy(n => (n.EncounterId, n.BeltId, n.Slot)).Select(g => g.MaxBy(n => n.Id)!))
        {
            if (latest.DestroyedAt is { } destroyed && latest.RetiredAt is null)
            {
                due = Math.Min(due, checked(destroyed + NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond));
            }
        }
        // Unsupported catalog pools can remain due; retry at most once per second.
        return due <= now ? checked(now + TimeSpan.TicksPerSecond) : due;
    }

    private sealed record PopulationSlot(long Id, long EncounterId, int BeltId, int Slot, long? DestroyedAt, long? RetiredAt);

    private static long? CalmMiningDeadline(WorldAction world, WorldNpcEncounter encounter, long now)
    {
        var operation = world.Set<WorldNpcMiningOperation>().Find(encounter.Id);
        if (
            operation is not { Stage: NpcMiningStage.Working }
            || operation.PickupAt <= now
            || world.Set<SavedNpcStanding>().Any(s => s.SourceId == operation.CorporationId && s.Value <= NpcMiningRules.HostileStanding)
        )
        {
            return null;
        }
        var active = world
            .Filter<WorldNpc>(n =>
                n.EncounterId == encounter.Id
                && n.DestroyedAt == null
                && n.RetiredAt == null
                && n.AnchorKind != NpcAnchorKind.Anomaly
                && n.AnchorKind != NpcAnchorKind.Mission
            )
            .ToArray();
        if (
            active.Length != NpcMiningRules.MinerCount
            || active.Any(n => n.Role != NpcRole.MiningMiner || n.Navigation != NpcNavigationState.Idle)
        )
        {
            return null;
        }
        var members = world.Filter<WorldNpcMiningMember>(m => m.EncounterId == encounter.Id).ToArray();
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
