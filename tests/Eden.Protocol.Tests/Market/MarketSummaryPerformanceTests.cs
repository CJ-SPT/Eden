using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Market;

public sealed class MarketSummaryPerformanceTests(ITestOutputHelper output)
{
    [Fact]
    public void RegionalSummarySelectsBestAsksAcrossAPopulatedRegion()
    {
        const int firstType = 100_000;
        const int typeCount = 500;
        const int stations = 3;
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var pilot = fixture.Store.FindPilot(1000001)!;
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath);
        int region = catalog.RequireSystem(pilot.SystemId).RegionId;
        using (WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None })
        {
            for (int station = 0; station < stations; station++)
            {
                for (int type = firstType; type < firstType + typeCount; type++)
                {
                    world.Add(
                        new NpcMarketOrder
                        {
                            StationId = pilot.StationId + station,
                            SystemId = pilot.SystemId + station / 2,
                            RegionId = region,
                            OwnerId = 1000001,
                            TypeId = type,
                            PriceCents = station == 0 ? 200 : 100,
                            Entered = 5,
                            Remaining = 5,
                            Issued = clock.Now,
                        }
                    );
                }
            }
            world.Add(
                new SavedPersonalOrder
                {
                    Id = PersonalMarketRules.FirstOrderId + 1,
                    PilotId = pilot.PilotId,
                    StationId = pilot.StationId,
                    SystemId = pilot.SystemId,
                    RegionId = region,
                    TypeId = firstType,
                    PriceCents = 75,
                    Entered = 3,
                    Remaining = 3,
                    Issued = clock.Now,
                    Expires = clock.Now + TimeSpan.TicksPerDay,
                    DurationDays = 1,
                    Status = PersonalOrderStatus.Open,
                }
            );
            world.SaveChanges();
        }
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        fixture.Services.GetRequiredService<LiveWorld>().Economy.Warm();
        const string cacheMiss = "populated region cache miss";
        PerformanceSample measured = default;
        IReadOnlyList<MarketOrder> first;
        using (SessionPerformance.Capture(cacheMiss, RecordSample))
        {
            first = market.BestAsks(pilot.PilotId, MarketScope.Region);
        }
        Assert.InRange(measured.AllocatedBytes, 1, 16 * 1024 * 1024);
        Assert.InRange(measured.Commands, 0, 5);
        Assert.InRange(measured.Connections, 0, 2);
        Assert.Equal(0, measured.Fittings);
        var seeded = first.Where(o => o.TypeId >= firstType).ToDictionary(o => o.TypeId);
        Assert.Equal(typeCount, seeded.Count);
        Assert.Equal(75, seeded[firstType].PriceCents);
        Assert.All(
            seeded.Values.Where(o => o.TypeId != firstType),
            order =>
            {
                Assert.Equal(100, order.PriceCents);
                Assert.Equal(pilot.StationId + 1, order.StationId);
            }
        );
        Assert.Equal(first.OrderBy(o => o.Id), first);
        using (var warm = SessionPerformance.Begin("populated region cache hit", output.WriteLine, force: true)!)
        {
            Assert.Same(first, market.BestAsks(pilot.PilotId, MarketScope.Region));
            Assert.Equal(0, warm.Commands);
            Assert.Equal(0, warm.Connections);
            Assert.Equal(0, warm.FittingCalculations);
        }
        using var scope = fixture.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(fixture.Store, p, clock, new(), fixture.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var wire = Assert.IsType<Dictionary<long, object?>>(
            router.CallWire(context, "marketProxy", "GetRegionBest", Array.Empty<object?>(), _ => { })
        );
        Assert.Equal(first.Count, wire.Count);
        Assert.Equal(BlueMarshal.Encode(MarketWire.Row(seeded[firstType])), BlueMarshal.Encode(wire[firstType]));

        void RecordSample(PerformanceSample sample)
        {
            output.WriteLine(sample.ToString());
            if (sample.Name == cacheMiss)
            {
                measured = sample;
            }
        }
    }

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
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void UnrelatedOffersKeepTheWarmSummarySqlFree(bool priceChange, bool localBid, bool resident)
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
        if (resident)
        {
            using var action = fixture.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
            var remote = action.Set<NpcMarketOrder>().Find(remoteId)!;
            if (priceChange)
            {
                remote.PriceCents--;
            }
            else
            {
                remote.Remaining = 3;
            }
            action.Commit();
        }
        else
        {
            using WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None };
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CommittedChangesAndExactExpiryInvalidateTheSummary(bool resident)
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        var pilot = fixture.Store.FindPilot(1000001)!;
        Seed(fixture, clock);
        var first = market.BestAsks(pilot.PilotId, MarketScope.Region);
        if (resident)
        {
            using var action = fixture.Services.GetRequiredService<LiveWorld>().Economy.Begin(TransactionParticipants.None);
            action.Set<NpcMarketOrder>().Find(first[0].Id)!.Remaining = 3;
            action.Commit();
        }
        else
        {
            using WorldDbContext world = new(fixture.Path) { Participants = TransactionParticipants.None };
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
    public void ResidentOrderMembershipChangeInvalidatesItsPreviousRegion()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var pilot = fixture.Store.FindPilot(1000001)!;
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        Seed(fixture, clock);
        var original = market.BestAsks(pilot.PilotId, MarketScope.Region);
        var order = Assert.Single(original);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        using (var rolledBack = live.Economy.Begin(TransactionParticipants.None))
        {
            rolledBack.Set<NpcMarketOrder>().Find(order.Id)!.RegionId++;
        }
        Assert.Same(original, market.BestAsks(pilot.PilotId, MarketScope.Region));
        using (var committed = live.Economy.Begin(TransactionParticipants.None))
        {
            committed.Set<NpcMarketOrder>().Find(order.Id)!.RegionId++;
            committed.Commit();
        }
        Assert.Empty(market.BestAsks(pilot.PilotId, MarketScope.Region));
    }

    [Fact]
    public void ResidentNpcAssortmentRejectsDuplicatesAndAllowsReplacingAnOrder()
    {
        TestClock clock = new();
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        Seed(fixture, clock);
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        NpcMarketOrder committed;
        using (var read = live.Economy.Begin(TransactionParticipants.None))
        {
            committed = read.Set<NpcMarketOrder>().Single();
        }
        using (var failed = live.Economy.Begin(TransactionParticipants.None))
        {
            failed.Add(Replacement(committed));
            Assert.Throws<InvalidDataException>(failed.Commit);
        }
        using (var failed = live.Economy.Begin(TransactionParticipants.None))
        {
            var original = failed.Set<NpcMarketOrder>().Single();
            failed.Remove(original);
            failed.Add(Replacement(original));
            failed.Add(Replacement(original));
            Assert.Throws<InvalidDataException>(failed.Commit);
        }
        using (var successful = live.Economy.Begin(TransactionParticipants.None))
        {
            var original = successful.Set<NpcMarketOrder>().Single();
            successful.Remove(original);
            successful.Add(Replacement(original));
            successful.Commit();
        }
        using var verified = live.Economy.Begin(TransactionParticipants.None);
        Assert.Equal(2, Assert.Single(verified.Set<NpcMarketOrder>()).Remaining);

        static NpcMarketOrder Replacement(NpcMarketOrder original) =>
            new()
            {
                StationId = original.StationId,
                SystemId = original.SystemId,
                RegionId = original.RegionId,
                OwnerId = original.OwnerId,
                TypeId = original.TypeId,
                Kind = original.Kind,
                Bid = original.Bid,
                PriceCents = original.PriceCents,
                Entered = 2,
                Remaining = 2,
                Issued = original.Issued,
            };
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
