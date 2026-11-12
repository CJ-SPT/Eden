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
        // Direct role tests register the systems containing saved fixture pilots without selecting an observer.
        int[] systems;
        using (WorldDbContext world = new(database.Path))
        {
            var agents = world.Set<SavedCapsuleerAgent>().Select(agent => agent.PilotId).ToArray();
            systems = world
                .Pilots.Where(pilot => Enumerable.Contains(agents, pilot.Id))
                .Select(pilot => new
                {
                    pilot.ActiveShip.StationId,
                    SystemId = pilot.ActiveShip.Position == null ? null : pilot.ActiveShip.Position.SystemId,
                })
                .AsEnumerable()
                .Select(ship => ship.SystemId ?? database.Catalog.FindStation(ship.StationId)!.SolarSystemId)
                .Distinct()
                .ToArray();
        }
        var scheduler = host.GetRequiredService<CapsuleerScheduler>();
        foreach (int system in systems)
        {
            scheduler.PopulationPrepared(system);
        }
        return host;
    }
}
