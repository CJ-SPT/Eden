using Eden.Gameplay.Capsuleers;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerTrafficObservationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OneHourResidentialAndQuietObservationsRetainLocalVisitsAndQuietSpace(bool quiet)
    {
        using CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false, isolateRoute: !quiet);
        using var host = ServerServices.Build(fixture.Database, fixture.Clock, liveWorld: true);
        const int homeSystem = 30000141;
        const int quietSystem = 30000144;
        const int homeStation = 60015157;
        using var selected = host.GetRequiredService<IHumanPresence>().Select(90000001, quiet ? quietSystem : homeSystem);
        var population = host.GetRequiredService<ICapsuleerPopulation>();
        var scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        var maintenance = host.GetRequiredService<CapsuleerMaintenance>();
        var pilots = population
            .Agents(quiet ? quietSystem : homeSystem)
            .Where(agent => agent.Career == CapsuleerCareer.Hauling)
            .Select(agent => agent.PilotId)
            .ToArray();
        Dictionary<long, int> locations = [];
        HashSet<long> docked = [];
        int homeVisits = 0;
        int crossings = 0;
        for (int second = 0; second <= 3600; second++)
        {
            foreach (long pilot in pilots)
            {
                var observation = scheduler.Hauler(pilot)!;
                Assert.Null(observation.Work.WaitingReason);
                if (locations.TryGetValue(pilot, out int system) && system != observation.Pilot.SystemId)
                {
                    crossings++;
                }
                locations[pilot] = observation.Pilot.SystemId;
                if (observation.Pilot.Position is null && observation.Pilot.StationId == homeStation)
                {
                    if (docked.Add(pilot))
                    {
                        homeVisits++;
                    }
                }
                else
                {
                    docked.Remove(pilot);
                }
            }
            fixture.Clock.Seconds++;
            maintenance.Tick();
        }
        output.WriteLine(
            $"One-hour quiet={quiet}: distinct local pilots={pilots.Length}, home visits={homeVisits}, crossings={crossings}."
        );
        if (quiet)
        {
            Assert.Empty(pilots);
            Assert.Equal(0, crossings);
        }
        else
        {
            Assert.NotEmpty(pilots);
            Assert.True(homeVisits >= 2);
        }
    }
}
