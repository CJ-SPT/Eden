using System.Diagnostics;
using Eden.Data.World.Exploration;
using Eden.Data.World.Mining;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Exploration;
using Eden.World.Mining;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Exploration;

public sealed class SiteHistoryPerformanceTests(ITestOutputHelper output)
{
    public enum SiteKind
    {
        Combat,
        Hacking,
        Gas,
    }

    private const int RemoteSystem = 30000142;
    private const int History = 4096;
    private const long HistoryOffset = 1000;

    [Theory]
    [InlineData(SiteKind.Combat)]
    [InlineData(SiteKind.Hacking)]
    [InlineData(SiteKind.Gas)]
    public async Task DiscoverySelectsCurrentSitesAndFreshIdsWithLocalAndRemoteHistory(SiteKind kind)
    {
        long baseline = await Measure(0);
        long populated = await Measure(History);
        const int scalarBytesPerSite = 1536;
        const int hackingBytesPerSite = 2560;
        int budget = kind == SiteKind.Hacking ? hackingBytesPerSite : scalarBytesPerSite;
        Assert.InRange(populated, 0, baseline + History * budget);

        async Task<long> Measure(int count)
        {
            TestClock clock = new();
            using TestWorld fixture = new(clock: clock, liveWorld: true);
            WorldDatabase database = new(fixture.Path);
            PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
            PassiveCatalogFixture.LoadHacking(database.CatalogPath);
            PassiveCatalogFixture.LoadMining(database.CatalogPath);
            var live = fixture.Services.GetRequiredService<LiveWorld>();
            int system = CombatAnomalyRules.SystemId;
            IReadOnlyList<CombatAnomaly> Read() =>
                kind switch
                {
                    SiteKind.Combat => new CombatAnomalyStore(database).List(system, clock.Now),
                    SiteKind.Hacking => ActivatorUtilities
                        .CreateInstance<PirateExplorationStore>(fixture.Services)
                        .Signatures(system, clock.Now),
                    SiteKind.Gas => new MiningSiteStore(database).Signatures(system, clock.Now),
                    _ => throw new InvalidOperationException(),
                };
            var initial = Read();
            Assert.NotEmpty(initial);
            var template = initial[0];
            long first = kind switch
            {
                SiteKind.Combat => CombatAnomalyRules.FirstSiteId,
                SiteKind.Hacking => HackingRules.FirstSiteId,
                _ => MiningSiteRules.FirstSiteId,
            };
            long highest = first + HistoryOffset + count;
            using (var setup = live.Economy.Begin(new(systems: [system, RemoteSystem])))
            {
                for (int index = 0; index <= count; index++)
                {
                    long id = first + HistoryOffset + index;
                    int location = index == count ? RemoteSystem : system;
                    switch (kind)
                    {
                        case SiteKind.Combat:
                            setup.Add(
                                new WorldCombatAnomaly
                                {
                                    Id = id,
                                    SystemId = location,
                                    DungeonId = template.DungeonId,
                                    CompletedAt = clock.Now - 1,
                                    ReplaceAt = clock.Now,
                                }
                            );
                            break;
                        case SiteKind.Hacking:
                            setup.Add(
                                new SavedExplorationSite
                                {
                                    Id = id,
                                    SystemId = location,
                                    DungeonId = template.DungeonId,
                                    Kind = (int)template.Exploration!.Value,
                                    ExpiresAt = clock.Now - 1,
                                    ReplaceAt = clock.Now,
                                }
                            );
                            break;
                        case SiteKind.Gas:
                            setup.Add(
                                new SavedMiningSite
                                {
                                    Id = id,
                                    SystemId = location,
                                    DungeonId = template.DungeonId,
                                    Family = MiningFamily.Gas,
                                    ExpiresAt = clock.Now - 1,
                                    ReplaceAt = clock.Now,
                                }
                            );
                            break;
                    }
                }
                setup.Commit();
            }
            // Refresh a warmed catalog through a fresh reader, retaining real resident history.
            IReadOnlyList<CombatAnomaly> current;
            long allocated;
            using (var metrics = SessionPerformance.Begin("historical site discovery", output.WriteLine, force: true)!)
            {
                allocated = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                current = Read();
                allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
                output.WriteLine(
                    $"Kind={kind}, history={count}, allocated={allocated}, "
                        + $"elapsed={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms."
                );
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            Assert.NotEmpty(current);
            Assert.DoesNotContain(current, site => site.Id >= first + HistoryOffset && site.Id <= highest);
            if (count > 0)
            {
                Assert.Contains(current, site => site.Id == highest + 1);
            }
            Assert.All(current, site => Assert.Equal(system, site.SystemId));
            long checkpointStarted = Stopwatch.GetTimestamp();
            await live.FlushAsync();
            output.WriteLine(
                $"Kind={kind}, history={count}, checkpoint={Stopwatch.GetElapsedTime(checkpointStarted).TotalMilliseconds:F2} ms."
            );
            using var disk = WorldConnections.FileConnection(fixture.Path);
            using WorldDbContext reopened = new(disk);
            foreach (var site in current)
            {
                bool saved = kind switch
                {
                    SiteKind.Combat => reopened.Set<WorldCombatAnomaly>().Find(site.Id) is not null,
                    SiteKind.Hacking => reopened.Set<SavedExplorationSite>().Find(site.Id) is not null,
                    _ => reopened.Set<SavedMiningSite>().Find(site.Id) is not null,
                };
                Assert.True(saved);
            }
            return allocated;
        }
    }
}
