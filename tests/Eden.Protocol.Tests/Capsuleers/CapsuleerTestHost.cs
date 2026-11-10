using Eden.Data.World.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

internal static class CapsuleerTestHost
{
    internal static ServiceProvider WithPreparedCommunities(this ServiceProvider host, WorldDatabase database)
    {
        if (LiveWorld.Find(database.Path) is null)
        {
            return host;
        }
        // These tests execute saved jobs directly without entering their region.
        // Register the fixture's prepared communities explicitly after regional startup.
        int[] systems;
        using (WorldDbContext world = new(database.Path))
        {
            systems = world.Set<SavedCapsuleerPopulation>().AsNoTracking().Select(population => population.SystemId).ToArray();
        }
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        foreach (int system in systems)
        {
            scheduler.PopulationPrepared(system);
        }
        return host;
    }
}
