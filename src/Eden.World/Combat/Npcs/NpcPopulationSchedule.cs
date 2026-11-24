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
        var resident = world.Set<WorldNpcEncounter>() as EconomySet<WorldNpcEncounter, EconomyWorldNpcEncounter>;
        var encounters = resident is not null
            ? resident
                .IndexedSnapshots(nameof(WorldNpcEncounter.SystemId), system)
                .Where(e => e.SystemId == system)
                .Select(e => new EncounterDeadline(e.Id, e.Role, e.Occupied, e.NextSpawnAt))
            : world
                .Filter<WorldNpcEncounter>(e => e.SystemId == system)
                .AsEnumerable()
                .Select(e => new EncounterDeadline(e.Id, e.Role, e.Occupied, e.NextSpawnAt))
                .ToArray();
        long due = long.MaxValue;
        foreach (var encounter in encounters)
        {
            if (!encounter.Occupied || encounter.NextSpawnAt is not long next)
            {
                continue;
            }
            if (encounter.Role == NpcRole.MiningMiner)
            {
                next = CalmMiningDeadline(world, encounter, now) ?? next;
            }
            due = Math.Min(due, next);
        }
        var members = NpcPopulationMember.InSystem(world, system);
        if (resident is null)
        {
            members = members.ToArray();
        }
        HashSet<long>? tracked = null;
        bool UsesSlotRespawn(NpcPopulationMember member)
        {
            // Authored encounters advance their own objectives. Guards retain per-slot
            // respawns, and legacy NPCs have no encounter row in this system.
            return !IsObjectiveAnchor(member.AnchorKind)
                && (
                    member.Role is NpcRole.Navy or NpcRole.Customs
                    || (
                        resident is not null
                            ? resident.ReadSnapshot(new(member.EncounterId))?.SystemId != system
                            : !(tracked ??= encounters.Select(e => e.Id).ToHashSet()).Contains(member.EncounterId)
                    )
                );
        }
        if (members.Any(n => n.DestroyedAt != null && n.RetiredAt == null && UsesSlotRespawn(n)))
        {
            var latestMembers = members
                .Where(UsesSlotRespawn)
                .GroupBy(n => (n.EncounterId, n.BeltId, n.Slot))
                .Select(g => g.MaxBy(n => n.Id));
            foreach (var latest in latestMembers)
            {
                if (latest.DestroyedAt is { } destroyed && latest.RetiredAt is null)
                {
                    due = Math.Min(due, checked(destroyed + NpcEncounterDefinitions.RespawnSeconds * TimeSpan.TicksPerSecond));
                }
            }
        }
        // Unsupported catalog pools can remain due; retry at most once per second.
        return due <= now ? checked(now + TimeSpan.TicksPerSecond) : due;
    }

    private static bool IsObjectiveAnchor(NpcAnchorKind kind) =>
        kind
            is NpcAnchorKind.Anomaly
                or NpcAnchorKind.Mission
                or NpcAnchorKind.GhostSite
                or NpcAnchorKind.Abyssal
                or NpcAnchorKind.Incursion
                or NpcAnchorKind.MilitiaComplex
                or NpcAnchorKind.MilitiaHub
                or NpcAnchorKind.MilitiaObjective;

    private readonly record struct EncounterDeadline(long Id, NpcRole Role, bool Occupied, long? NextSpawnAt);

    private readonly record struct MiningDeadline(long NpcId, long? MiningDueAt, long? WarpStartedAt);

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
        var active = NpcPopulationMember.AtAnchor(world, encounter.Id).Where(n => n.Active && !IsObjectiveAnchor(n.AnchorKind)).ToArray();
        if (
            active.Length != NpcMiningRules.MinerCount
            || active.Any(n => n.Role != NpcRole.MiningMiner || n.Navigation != NpcNavigationState.Idle)
        )
        {
            return null;
        }
        var members = world.Set<WorldNpcMiningMember>() is EconomySet<WorldNpcMiningMember, EconomyWorldNpcMiningMember> resident
            ? resident
                .IndexedSnapshots(nameof(WorldNpcMiningMember.EncounterId), encounter.Id)
                .Where(m => m.EncounterId == encounter.Id)
                .Select(m => new MiningDeadline(m.NpcId, m.MiningDueAt, m.WarpStartedAt))
            : world
                .Filter<WorldNpcMiningMember>(m => m.EncounterId == encounter.Id)
                .AsEnumerable()
                .Select(m => new MiningDeadline(m.NpcId, m.MiningDueAt, m.WarpStartedAt));
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
