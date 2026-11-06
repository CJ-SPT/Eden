using Eden.Gameplay.Combat;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class NpcPopulationSchedule
{
    internal static long Next(WorldDbContext world, int system, long now)
    {
        var encounters = world.Set<WorldNpcEncounter>().AsNoTracking().Where(e => e.SystemId == system).ToArray();
        long due = encounters
            .Where(e => e.Occupied && e.NextSpawnAt != null)
            .Select(e => e.NextSpawnAt!.Value)
            .DefaultIfEmpty(long.MaxValue)
            .Min();
        // Guards respawn per slot. Legacy fixture pirates have no encounter row.
        var tracked = encounters.Select(e => e.Id).ToHashSet();
        var rows = world.Set<WorldNpc>().AsNoTracking().Where(n => n.SystemId == system).ToArray();
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
}
