using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class FreshUniverseTests
{
    [InstalledCatalogFact]
    public async Task FreshPopulationPreparesUnvisitedWorkSystemsWhileSomeonePlaysAndStopsAtTheCap()
    {
        var directory = TestDirectory.Create("eden-fresh-population-");
        try
        {
            const int cap = 64;
            const long seed = 3662168;
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
            TestClock clock = new();
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            WorldConfigurationStore configuration = new(database, clock);
            var state = configuration.Read();
            var settings = state.Active.Draft.Settings;
            var policy = settings.Capsuleers;
            int starterRegion = database.Catalog.Systems[StarterSystem].RegionId;
            configuration.Save(
                state.Generation,
                state.Active.Draft with
                {
                    Settings = settings with
                    {
                        Capsuleers = policy with
                        {
                            ActivePilotCap = cap,
                            DefaultRegionAccess = CapsuleerRegionAccess.TransitOnly,
                            RegionAccess = policy.RegionAccess.Add(starterRegion, CapsuleerRegionAccess.WorkAndTransit),
                        },
                    },
                },
                "Population replay"
            );
            WorldInitialization.Initialize(database, clock);

            long[] pilots;
            using (var host = ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true))
            {
                var live = host.GetRequiredService<LiveWorld>();
                var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
                Assert.True(preparation.PrepareNext().Complete);
                Assert.Empty(live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().ToArray()));
                using (var world = live.Economy.Begin())
                {
                    world.Add(new SavedCapsuleerProvisioning { Id = 1, WorldSeed = seed });
                    world.Commit();
                }

                var presence = host.GetRequiredService<IHumanPresence>();
                using var observer = presence.Select(90000001, StarterSystem);
                PerformanceSample sample = default;
                using (SessionPerformance.Capture("fresh population preparation", value => sample = value, includePhases: false))
                {
                    Assert.Equal(StarterSystem, preparation.PrepareNext().SystemId);
                    for (int attempt = 0; attempt < 250; attempt++)
                    {
                        var turn = preparation.PrepareNext();
                        Assert.Null(turn.Failure);
                        if (turn.Complete)
                        {
                            break;
                        }
                        clock.Seconds++;
                    }
                }
                var founders = live.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray());
                Assert.Equal(cap, founders.Length);
                output.WriteLine($"Prepared {cap} pilots in {sample.Elapsed.TotalMilliseconds:F2} ms / {sample.AllocatedBytes:N0} bytes.");
                output.WriteLine($"SQL commands: {sample.Commands}. Connections: {sample.Connections}. Fittings: {sample.Fittings}.");
                Assert.Contains(founders, agent => agent.CommunitySystemId == StarterSystem);
                Assert.Contains(founders, agent => agent.CommunitySystemId != StarterSystem);
                Assert.All(founders, agent => Assert.Equal(starterRegion, database.Catalog.Systems[agent.CommunitySystemId].RegionId));
                Assert.Equal([StarterSystem], presence.Read().ActiveSystems);
                Assert.True(preparation.PrepareNext().Complete);
                pilots = founders.Select(agent => agent.PilotId).ToArray();
                await live.FlushAsync();
            }

            WorldInitialization.Initialize(database, clock);
            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            var restored = reopened.GetRequiredService<LiveWorld>();
            Assert.Equal(
                pilots,
                restored.Economy.Inspect(world => world.Set<SavedCapsuleerAgent>().Select(agent => agent.PilotId).Order().ToArray())
            );
            Assert.Equal(cap, reopened.GetRequiredService<ICapsuleerPopulation>().Summary().EnabledAgents);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
