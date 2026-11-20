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
    internal static IEnumerable<long> HostilePilots(WorldAction world, long corporation) =>
        world.Set<SavedNpcStanding>() is EconomySet<SavedNpcStanding, EconomySavedNpcStanding> resident
            ? resident
                .IndexedSnapshots(nameof(SavedNpcStanding.SourceId), corporation)
                .Where(s => s.SourceId == corporation && s.Value <= NpcMiningRules.HostileStanding)
                .Select(s => s.PilotId)
            : world
                .Filter<SavedNpcStanding>(s => s.SourceId == corporation && s.Value <= NpcMiningRules.HostileStanding)
                .Select(s => s.PilotId);

    internal static long Next(WorldDbContext world, int system, long now) => Next(new SqlWorldAction(world), system, now);

    internal static long Next(WorldAction world, int system, long now)
    {
        var encounters = world.Set<WorldNpcEncounter>() is EconomySet<WorldNpcEncounter, EconomyWorldNpcEncounter> resident
            ? resident
                .IndexedSnapshots(nameof(WorldNpcEncounter.SystemId), system)
                .Where(e => e.SystemId == system)
                .Select(e => new EncounterDeadline(e.Id, e.Role, e.Occupied, e.NextSpawnAt))
                .ToArray()
            : world
                .Filter<WorldNpcEncounter>(e => e.SystemId == system)
                .AsEnumerable()
                .Select(e => new EncounterDeadline(e.Id, e.Role, e.Occupied, e.NextSpawnAt))
                .ToArray();
        // Guards respawn per slot. Legacy fixture pirates have no encounter row.
        var tracked = encounters.Select(e => e.Id).ToHashSet();
        // Authored anomaly and mission losses advance their own objectives. They never
        // use regional slot respawns, even without a regional encounter row.
        var slots = NpcPopulationMember
            .InSystem(world, system)
            .Where(n =>
                n.AnchorKind is not (NpcAnchorKind.Anomaly or NpcAnchorKind.Mission or NpcAnchorKind.GhostSite or NpcAnchorKind.Abyssal)
                && (n.Role is NpcRole.Navy or NpcRole.Customs || !tracked.Contains(n.EncounterId))
            )
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

    private readonly record struct EncounterDeadline(long Id, NpcRole Role, bool Occupied, long? NextSpawnAt);

    private static long? CalmMiningDeadline(WorldAction world, EncounterDeadline encounter, long now)
    {
        var operation = world.Set<WorldNpcMiningOperation>().Find(encounter.Id);
        if (
            operation is not { Stage: NpcMiningStage.Working }
            || operation.PickupAt <= now
            || HostilePilots(world, operation.CorporationId).Any()
        )
        {
            return null;
        }
        var active = NpcPopulationMember
            .AtAnchor(world, encounter.Id)
            .Where(n =>
                n.Active
                && n.AnchorKind is not (NpcAnchorKind.Anomaly or NpcAnchorKind.Mission or NpcAnchorKind.GhostSite or NpcAnchorKind.Abyssal)
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
