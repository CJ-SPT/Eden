using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketSummaryPerformanceTests
{
    [Fact]
    public void InstitutionalPreparationIsPublishedOnlyAfterCommitAndRollbackCanRetry()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        WorldDatabase database = new(fixture.Path);
        using (CatalogDbContext edit = new(database.CatalogPath, writable: true))
        {
            edit.Database.ExecuteSqlRaw(
                """
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT Id+1,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z
                    FROM Station WHERE Id=60003760;
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT Id+2,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z
                    FROM Station WHERE Id=60003760;
                INSERT OR IGNORE INTO ItemGroup VALUES(31,'Shuttle',6);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES(672,'Caldari Shuttle',31);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/stationoperations','14','{{"services":[7]}}'),
                    ('client/types','672','{{"published":1,"marketGroupID":1}}'),
                    ('client/npccorporations','1000035','{{"corporationTrades":{{"672":1}}}}');
                """
            );
        }
        using (WorldDbContext settings = new(fixture.Path))
        {
            settings.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            settings.SaveChanges();
        }
        var pilot = fixture.Store.FindPilot(1000001)!;
        var market = fixture.Services.GetRequiredService<PilotMarketStore>();
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using CatalogDbContext catalog = new(database.CatalogPath);
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            using (var failed = world.Database.BeginTransaction())
            {
                Assert.Contains(market.SupplyOrders(world, catalog, pilot.StationId, [672]), order => order.TypeId == 672);
                failed.Rollback();
            }
            world.ChangeTracker.Clear();
            Assert.False(live.InstitutionalSupply.ContainsKey(pilot.StationId));
            Assert.False(
                WorldConnections.PreparedInstitutional(
                    world,
                    new(
                        pilot.StationId,
                        catalog.Definitions.Identity.Generation,
                        world.Set<SavedWorldEconomy>().Single().InstitutionalStock
                    )
                )
            );
            Assert.Empty(world.Set<NpcMarketOrder>().Where(order => order.StationId == pilot.StationId && order.TypeId == 672));
            using var successful = world.Database.BeginTransaction();
            Assert.Contains(market.SupplyOrders(world, catalog, pilot.StationId, [672]), order => order.TypeId == 672);
            Assert.Contains(market.SupplyOrders(world, catalog, pilot.StationId + 1, [672]), order => order.TypeId == 672);
            successful.Commit();
        }
        Assert.True(live.InstitutionalSupply.ContainsKey(pilot.StationId));
        Assert.True(live.InstitutionalSupply.ContainsKey(pilot.StationId + 1));
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            using var transaction = world.Database.BeginTransaction();
            Assert.Contains(market.SupplyOrders(world, catalog, pilot.StationId + 2, [672]), order => order.TypeId == 672);
            transaction.Commit();
        }
        Assert.True(live.InstitutionalSupply.ContainsKey(pilot.StationId));
        Assert.True(live.InstitutionalSupply.ContainsKey(pilot.StationId + 1));
        Assert.True(live.InstitutionalSupply.ContainsKey(pilot.StationId + 2));
        using (WorldDbContext stock = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            stock
                .Set<NpcMarketOrder>()
                .Where(o => o.StationId == pilot.StationId && o.TypeId == 672)
                .ExecuteUpdate(setters => setters.SetProperty(o => o.Remaining, 0));
        }
        Assert.True(live.InstitutionalSupply.ContainsKey(pilot.StationId));
        Assert.Contains(market.BestAsks(pilot.PilotId, MarketScope.Station), order => order.TypeId == 672 && order.Remaining > 0);
    }

    private static void Seed(TestWorld fixture, TestClock clock)
    {
        var pilot = fixture.Store.FindPilot(1000001)!;
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath);
        using WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None };
        world.Add(
            new NpcMarketOrder
            {
                StationId = pilot.StationId,
                SystemId = pilot.SystemId,
                RegionId = catalog.RequireSystem(pilot.SystemId).RegionId,
                OwnerId = 1000001,
                TypeId = pilot.Ship.TypeId,
                Bid = false,
                Kind = world.Set<SavedWorldEconomy>().Single().TestingMarket ? NpcMarketKind.Development : NpcMarketKind.Institutional,
                PriceCents = 12345,
                Entered = 5,
                Remaining = 5,
                Issued = clock.Now,
            }
        );
        world.SaveChanges();
    }

    [Fact]
    public void WarmSummaryIsSqlFreeAndRollbackDoesNotInvalidateIt()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        var pilot = fixture.Store.FindPilot(1000001)!;
        Seed(fixture, clock);
        var first = market.BestAsks(pilot.PilotId, MarketScope.Region);
        Assert.NotEmpty(first);
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        using (var transaction = world.Database.BeginTransaction())
        {
            world.Set<NpcMarketOrder>().Where(o => o.Id == first[0].Id).ExecuteUpdate(s => s.SetProperty(o => o.Remaining, 0));
            transaction.Rollback();
        }
        using var metrics = SessionPerformance.Begin("unchanged summary", _ => { }, force: true)!;
        Assert.Same(first, market.BestAsks(pilot.PilotId, MarketScope.Region));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void UnrelatedOffersKeepTheWarmSummarySqlFree(bool priceChange, bool localBid)
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        var pilot = fixture.Store.FindPilot(1000001)!;
        Seed(fixture, clock);
        long remoteId;
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            var local = world.Set<NpcMarketOrder>().Single();
            var remote = new NpcMarketOrder
            {
                StationId = local.StationId + 1,
                SystemId = local.SystemId + 1,
                RegionId = local.RegionId + (localBid ? 0 : 1),
                Bid = localBid,
                OwnerId = local.OwnerId,
                TypeId = local.TypeId,
                Kind = local.Kind,
                PriceCents = local.PriceCents,
                Entered = 5,
                Remaining = 5,
                Issued = clock.Now,
            };
            world.Add(remote);
            world.SaveChanges();
            remoteId = remote.Id;
        }
        var first = market.BestAsks(pilot.PilotId, MarketScope.Region);
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            var remote = world.Set<NpcMarketOrder>().Where(o => o.Id == remoteId);
            if (priceChange)
            {
                remote.ExecuteUpdate(setters => setters.SetProperty(o => o.PriceCents, o => o.PriceCents - 1));
            }
            else
            {
                remote.ExecuteUpdate(setters => setters.SetProperty(o => o.Remaining, 3));
            }
        }
        using var metrics = SessionPerformance.Begin("unrelated region summary", _ => { }, force: true)!;
        Assert.Same(first, market.BestAsks(pilot.PilotId, MarketScope.Region));
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Fact]
    public void CommittedChangesAndExactExpiryInvalidateTheSummary()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        var pilot = fixture.Store.FindPilot(1000001)!;
        Seed(fixture, clock);
        var first = market.BestAsks(pilot.PilotId, MarketScope.Region);
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            world.Set<NpcMarketOrder>().Where(o => o.Id == first[0].Id).ExecuteUpdate(s => s.SetProperty(o => o.Remaining, 3));
        }
        var second = market.BestAsks(pilot.PilotId, MarketScope.Region);
        Assert.NotSame(first, second);
        Assert.Equal(3, Assert.Single(second).Remaining);
        long expiry = second.Min(o => o.Issued) + TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks;
        clock.UtcNow = DateTimeOffset.FromFileTime(expiry - 1);
        Assert.Same(second, market.BestAsks(pilot.PilotId, MarketScope.Region));
        clock.UtcNow = DateTimeOffset.FromFileTime(expiry);
        var third = market.BestAsks(pilot.PilotId, MarketScope.Region);
        Assert.NotSame(second, third);
        Assert.All(third, order => Assert.True(order.Issued + TimeSpan.FromDays(MarketDefinitions.NpcOrderDays).Ticks > clock.Now));
    }

    [Fact]
    public async Task WarmSummaryCompletesWhileCheckpointCopyIsPaused()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock);
        using ManualResetEventSlim copying = new();
        using ManualResetEventSlim release = new();
        using LiveWorld live = new(
            new(fixture.Path),
            clock,
            _ => { },
            copy: (source, target) =>
            {
                copying.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
                source.BackupDatabase(target);
            }
        );
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        var pilot = fixture.Store.FindPilot(1000001)!;
        Seed(fixture, clock);
        var expected = market.BestAsks(pilot.PilotId, MarketScope.Region);
        var checkpoint = Task.Run(live.CaptureSnapshot);
        try
        {
            Assert.True(copying.Wait(TimeSpan.FromSeconds(10)));
            var read = Task.Run(() => market.BestAsks(pilot.PilotId, MarketScope.Region));
            Assert.Same(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(2))));
            Assert.Same(expected, await read);
        }
        finally
        {
            release.Set();
            using var snapshot = await checkpoint;
        }
    }
}
