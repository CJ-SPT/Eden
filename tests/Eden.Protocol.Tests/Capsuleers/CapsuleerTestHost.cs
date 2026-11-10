using Eden.Data.World.Capsuleers;
using Eden.World.Capsuleers;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

internal static class CapsuleerTestHost
{
    internal static void PrepareActivatedPopulations(this ServiceProvider host)
    {
        var preparation = host.GetRequiredService<Eden.Gameplay.Capsuleers.ICapsuleerBackgroundPreparation>();
        const int MaximumSteps = 100000;
        for (int step = 0; step < MaximumSteps; step++)
        {
            var turn = preparation.PrepareNext();
            Assert.Null(turn.Failure);
            if (turn.Complete)
            {
                return;
            }
        }
        Assert.Fail("Population preparation did not finish its bounded test workload.");
    }

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
