using Eden.Data.World.Capsuleers;
using Eden.Data.World.Market;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Server;
using Eden.World.Agents;
using Eden.World.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Industry;

public sealed partial class FreshUniverseTests
{
    [InstalledCatalogFact]
    public async Task FreshFounderCohortsExpandAndResumeWithoutReplacingPilots()
    {
        var directory = TestDirectory.Create("eden-fresh-cohorts-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            File.Copy(Environment.GetEnvironmentVariable(CatalogEnvironmentVariable)!, database.OriginalCatalogPath);
            TestClock clock = new();
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            WorldConfigurationStore configuration = new(database, clock);
            int system;
            using (CatalogDbContext catalog = new(database.CatalogPath))
            {
                system = NpcDirectory
                    .Agents(catalog)
                    .Where(agent =>
                        agent is { Type: AgentType.Basic, Level: 1, DivisionId: MissionRules.DistributionDivision, SystemId: > 0 }
                    )
                    .Select(agent => agent.SystemId!.Value)
                    .Distinct()
                    .First(id =>
                        CapsuleerProvisioningCatalog.StarterIndustry(catalog, id).Eligibility == CapsuleerEligibility.Eligible
                        && FreshCareerRoutes.Read(catalog, id) is { Courier: not null, Combat: not null }
                    );
            }

            SavePolicy(cohorts: FreshCareerRules.DefaultFounderCohorts, cap: 4);
            var original = await Prepare(expected: 4);
            SavePolicy(cohorts: FreshCareerRules.MaximumFounderCohorts, cap: 17);
            var partial = await Prepare(expected: 17);
            Assert.Equal(original, partial.Take(original.Length));

            SavePolicy(cohorts: FreshCareerRules.MaximumFounderCohorts, cap: 20);
            var expanded = await Prepare(expected: 20);
            Assert.Equal(partial, expanded.Take(partial.Length));
            Assert.Equal(
                Enumerable.Range(0, 10),
                expanded.Where(agent => agent.Career == CapsuleerCareer.Mining).Select(agent => agent.Slot)
            );
            Assert.Equal(
                Enumerable.Range(0, 10),
                expanded.Where(agent => agent.Career == CapsuleerCareer.Hauling).Select(agent => agent.Slot)
            );

            SavePolicy(cohorts: FreshCareerRules.DefaultFounderCohorts, cap: 20);
            Assert.Equal(expanded, await Prepare(expected: 4, countSaved: 20));
            SavePolicy(cohorts: FreshCareerRules.MaximumFounderCohorts, cap: 20);
            Assert.Equal(expanded, await Prepare(expected: 20));

            void SavePolicy(int cohorts, int cap)
            {
                var state = configuration.Read();
                var settings = state.Active.Draft.Settings;
                configuration.Save(
                    state.Generation,
                    state.Active.Draft with
                    {
                        Settings = settings with
                        {
                            Capsuleers = settings.Capsuleers with
                            {
                                ActivePilotCap = cap,
                                FreshFounderCohortsPerSystem = cohorts,
                                PrepareUnvisitedSystems = false,
                            },
                        },
                    },
                    "Fresh founder cohort replay"
                );
                WorldInitialization.Initialize(database, clock);
            }

            async Task<SavedFounder[]> Prepare(int expected, int? countSaved = null)
            {
                using var host = ServerServices.Build(database, clock, population: new TestNpcPopulationSeeder(), liveWorld: true);
                var live = host.GetRequiredService<LiveWorld>();
                using var observer = host.GetRequiredService<IHumanPresence>().Select(90000001, system);
                var preparation = host.GetRequiredService<ICapsuleerBackgroundPreparation>();
                var turn = preparation.PrepareNext();
                for (int attempt = 0; attempt < 25 && !turn.Complete; attempt++)
                {
                    Assert.Null(turn.Failure);
                    turn = preparation.PrepareNext();
                }
                Assert.Null(turn.Failure);
                Assert.True(turn.Complete);
                Assert.Equal(expected, host.GetRequiredService<ICapsuleerPopulation>().Summary().EnabledAgents);
                var roster = live.Economy.Inspect(world =>
                {
                    var agents = world.Set<SavedCapsuleerAgent>().OrderBy(agent => agent.PilotId).ToArray();
                    Assert.Equal(countSaved ?? expected, agents.Length);
                    Assert.All(
                        agents,
                        agent =>
                        {
                            Assert.Equal(system, agent.CommunitySystemId);
                            Assert.Equal(0, world.Set<PilotWallet>().Find(agent.PilotId)!.Cents);
                            Assert.NotNull(world.Set<SavedCapsuleerJob>().Find(agent.PilotId));
                            if (agent.Career == CapsuleerCareer.Hauling)
                            {
                                var saved = world.Filter<SavedCapsuleerHauler>(row => row.PilotId == agent.PilotId).Single();
                                var profession = agent.Slot % 2 == 0 ? CapsuleerProfession.Hauling : CapsuleerProfession.Combat;
                                Assert.Equal(profession, HaulerCheckpoint.Read(saved.Progress)?.CareerPlan?.Profession);
                            }
                        }
                    );
                    return agents.Select(agent => new SavedFounder(agent.PilotId, agent.FounderShipId, agent.Career, agent.Slot)).ToArray();
                });
                await live.FlushAsync();
                return roster;
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed record SavedFounder(long Pilot, long Ship, CapsuleerCareer Career, int Slot);

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
