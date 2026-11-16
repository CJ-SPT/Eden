using System.Collections.Concurrent;
using System.Diagnostics;
using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Exploration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Combat;

public sealed partial class ServerPerformanceReplayTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(4096)]
    public async Task TwoMovingSessionFramesRefreshSitesWithArchivedHistoryWithoutSql(int history)
    {
        const int remoteSystem = 30000142;
        const long historyOffset = 1000;
        int system = CombatAnomalyRules.SystemId;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadCombatAnomalyWorld(database.CatalogPath);
        PassiveCatalogFixture.LoadHacking(database.CatalogPath);
        PassiveCatalogFixture.LoadMining(database.CatalogPath);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var sites = fixture.Services.GetRequiredService<ICombatAnomalies>();
        var template = sites.List(system, clock.Now)[0];
        Assert.NotEmpty(sites.Signatures(system, clock.Now));
        var first = fixture.Store.FindPilot(1000001)!;
        var second = fixture.Store.FindPilot(1000002)!;
        fixture.Store.SetPosition(first.ShipId, new(1_000_000_000_000, 0, 0), system);
        fixture.Store.SetPosition(second.ShipId, new(1_000_000_000_000, 1000, 0), system);
        ConcurrentQueue<string> reports = new();
        using ReplayPlayer owner = new(fixture.Services, clock, first.PilotId, 1000001, reports);
        using ReplayPlayer observer = new(fixture.Services, clock, second.PilotId, 1000002, reports);
        owner.Context.Flight.Snapshot();
        observer.Context.Flight.Snapshot();
        Assert.Null(owner.Context.Flight.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        Assert.Null(observer.Context.Flight.CallWire("beyonce", "CmdGotoDirection", [1d, 0d, 0d]));
        for (int warmup = 0; warmup < 3; warmup++)
        {
            clock.Seconds++;
            await owner.Frame();
            await observer.Frame();
        }
        long highest = CombatAnomalyRules.FirstSiteId + historyOffset + history;
        using (var setup = live.Economy.Begin(new(systems: [system, remoteSystem])))
        {
            for (int index = 0; index <= history; index++)
            {
                setup.Add(
                    new WorldCombatAnomaly
                    {
                        Id = CombatAnomalyRules.FirstSiteId + historyOffset + index,
                        SystemId = index == history ? remoteSystem : system,
                        DungeonId = template.DungeonId,
                        CompletedAt = clock.Now - 1,
                        ReplaceAt = clock.Now,
                    }
                );
            }
            setup.Commit();
        }
        // Importing historical rows deliberately expires the existing discovery view.
        // This call updates its version without opening a database connection.
        using (WorldDbContext invalidation = new(fixture.Path))
        {
            CombatAnomalyStore.Invalidate(invalidation, system);
        }
        clock.Seconds++;
        using (var metrics = SessionPerformance.Begin("two frames with site history", _ => { }, force: true)!)
        {
            long allocated = GC.GetTotalAllocatedBytes();
            long started = Stopwatch.GetTimestamp();
            await owner.Frame();
            await observer.Frame();
            output.WriteLine(
                $"History={history}, two frames={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2} ms, "
                    + $"allocated={GC.GetTotalAllocatedBytes() - allocated}."
            );
            Assert.Equal(0, metrics.Commands);
            Assert.Equal(0, metrics.Connections);
            Assert.Equal(0, metrics.FittingCalculations);
        }
        if (history > 0)
        {
            Assert.Contains(sites.List(system, clock.Now), site => site.Id > highest);
        }
        Assert.True(owner.Context.Pilot.Position!.X > 1_000_000_000_000);
        Assert.True(observer.Context.Pilot.Position!.X > 1_000_000_000_000);
        long checkpointStarted = Stopwatch.GetTimestamp();
        await live.FlushAsync();
        output.WriteLine($"History={history}, checkpoint={Stopwatch.GetElapsedTime(checkpointStarted).TotalMilliseconds:F2} ms.");
    }
}
