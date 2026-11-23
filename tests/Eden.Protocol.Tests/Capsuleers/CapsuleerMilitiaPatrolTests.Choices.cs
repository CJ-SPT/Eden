using Eden.Data.World.FactionWarfare;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.FactionWarfare;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Capsuleers;
using Eden.World.FactionWarfare;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed partial class CapsuleerMilitiaPatrolTests
{
    [Fact]
    public void SeededPatrolChoicesVaryOnTheSameFrontAndFollowAChangedOccupation()
    {
        var scenario = PatrolScenario.WithTwoFrontlines(48);
        int alternative = Assert.IsType<int>(scenario.AlternativeFrontline);
        HashSet<int> visited = [];
        foreach (int seed in Enumerable.Range(1, 12))
        {
            int selected = ChooseFrontline(seed, adjustOccupation: visited.Count == 0);
            Assert.Contains(selected, new[] { scenario.Frontline, alternative });
            visited.Add(selected);
            if (visited.Count == 2)
            {
                break;
            }
        }
        Assert.Equal(2, visited.Count);

        int ChooseFrontline(int seed, bool adjustOccupation)
        {
            var directory = TestDirectory.Create("militia-patrol-choices-");
            try
            {
                WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
                TestClock clock = new(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero));
                PrepareCatalog(database.CatalogPath, scenario);
                Configure(database, clock);
                using var host = ServerServices.Build(database, clock, liveWorld: true);
                var live = host.GetRequiredService<LiveWorld>();
                StagePilots(live, database, clock, scenario, includeDefender: false, worldSeed: seed);
                using var human = host.GetRequiredService<IHumanPresence>().Select(Human, Jita);
                var scheduler = host.GetRequiredService<CapsuleerScheduler>();
                scheduler.PopulationPrepared(scenario.AttackerSystem);
                var worker = host.GetRequiredService<CapsuleerMaintenance>();
                var warzones = host.GetRequiredService<IFactionWarzones>();
                var complexes = host.GetRequiredService<IMilitiaComplexes>();
                foreach (int system in new[] { scenario.Frontline, alternative })
                {
                    Assert.Equal(WarzoneAdjacency.Frontline, warzones.System(system)?.Adjacency);
                    Assert.Equal(scenario.DungeonId, Assert.Single(complexes.Sites(system, clock.Now)).DungeonId);
                }

                var initial = AdvanceUntil(patrol => patrol.Phase == MilitiaPatrolPhase.Travelling);
                int selected = initial.Site.SystemId;
                output.WriteLine($"World seed {seed} selected militia site {initial.Site.Id} in system {selected}.");
                if (adjustOccupation)
                {
                    var affected = FactionWarzoneCatalog.Systems[selected].Neighbors.Add(selected);
                    using (var action = live.Economy.Begin(new(systems: affected)))
                    {
                        foreach (int system in affected)
                        {
                            var row =
                                action.Set<SavedWarzoneOccupation>().Find(system)
                                ?? action.Add(new SavedWarzoneOccupation { SystemId = system }).Entity;
                            row.Occupier = scenario.AttackerFaction;
                        }
                        action.Commit();
                    }
                    Assert.NotEqual(WarzoneAdjacency.Frontline, warzones.System(selected)?.Adjacency);
                    int remaining = selected == scenario.Frontline ? alternative : scenario.Frontline;
                    Assert.Equal(WarzoneAdjacency.Frontline, warzones.System(remaining)?.Adjacency);
                    AdvanceUntil(patrol => patrol.Phase == MilitiaPatrolPhase.Returning);
                    var reassigned = AdvanceUntil(patrol => patrol.Phase == MilitiaPatrolPhase.Holding);
                    Assert.Equal(remaining, reassigned.Site.SystemId);
                }
                return selected;

                MilitiaPatrolProgress AdvanceUntil(Func<MilitiaPatrolProgress, bool> condition)
                {
                    for (int turn = 0; turn < MaximumSeconds; turn++)
                    {
                        clock.Seconds++;
                        worker.Tick();
                        if (scheduler.Hauler(AttackerPilot)?.Work.MilitiaPatrol is { } patrol && condition(patrol))
                        {
                            return patrol;
                        }
                    }
                    var last = scheduler.Hauler(AttackerPilot);
                    throw new Xunit.Sdk.XunitException(
                        $"Seed {seed} did not reach the patrol state. Last activity: {last?.Work.Activity}, "
                            + $"site: {last?.Work.MilitiaPatrol?.Site.Id}, phase: {last?.Work.MilitiaPatrol?.Phase}, "
                            + $"system: {last?.Pilot.SystemId}."
                    );
                }
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }
    }
}
