using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class CapsuleerPopulationActivation
{
    // Restart-only configuration: reconcile before admitting observers or starting any jobs.
    internal static void Apply(WorldDatabase database, TimeProvider clock)
    {
        using var catalogScope = new CatalogReadScope().Begin();
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        using var transaction = world.Database.BeginTransaction();
        var policy = world.Settings.Capsuleers;
        long seed = world.Set<SavedCapsuleerProvisioning>().SingleOrDefault()?.WorldSeed ?? 0;
        long elapsed = world.Set<SavedCapsuleerClock>().Single().ElapsedTicks;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var suspended = world.Set<SavedCapsuleerSuspension>().ToDictionary(s => s.PilotId);
        foreach (var community in world.Set<SavedCapsuleerAgent>().AsNoTracking().ToArray().GroupBy(a => a.CommunitySystemId))
        {
            // Restoring saved policy limits needs local counts, not a route search for every dormant community.
            var traffic = CapsuleerTrafficRoutes.Geography(catalog, community.Key);
            int count = CapsuleerEncounterRules.Haulers(seed, community.Key, traffic.GateCount, traffic.StationCount, policy);
            foreach (var agent in community)
            {
                bool eligible = CapsuleerPopulationRules.Included(agent.Career, agent.Slot, count, policy);
                if (!eligible && !suspended.ContainsKey(agent.PilotId))
                {
                    world.Add(new SavedCapsuleerSuspension { PilotId = agent.PilotId, StartedTicks = elapsed });
                }
                else if (eligible && suspended.TryGetValue(agent.PilotId, out var pause))
                {
                    long delay = Math.Max(0, elapsed - pause.StartedTicks);
                    if (agent.Career == CapsuleerCareer.Mining)
                    {
                        var row = world.Set<SavedCapsuleerCommunity>().Single(c => c.PilotId == agent.PilotId);
                        if (MinerCheckpoint.Read(row.Progress) is { } work)
                        {
                            new MinerCheckpoint(agent.PilotId, CapsuleerPauseRules.Resume(work, delay)).Apply(world);
                        }
                    }
                    else
                    {
                        var row = world.Set<SavedCapsuleerHauler>().Single(h => h.PilotId == agent.PilotId);
                        if (HaulerCheckpoint.Read(row.Progress) is { } work)
                        {
                            new HaulerCheckpoint(agent.PilotId, CapsuleerPauseRules.Resume(work, delay, now)).Apply(world);
                        }
                    }
                    var job = world.Set<SavedCapsuleerJob>().Find(agent.PilotId)!;
                    job.DueTicks = Math.Max(checked(elapsed + TimeSpan.TicksPerSecond), checked(job.DueTicks + delay));
                    job.Revision = checked(job.Revision + 1);
                    world.Remove(pause);
                }
            }
        }
        world.SaveChanges();
        transaction.Commit();
    }
}
