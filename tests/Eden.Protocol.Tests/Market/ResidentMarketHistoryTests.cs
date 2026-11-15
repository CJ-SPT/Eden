using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Market;

public sealed class ResidentMarketHistoryTests(ITestOutputHelper output)
{
    private const int RequestedType = 34;
    private const int UnrelatedType = 35;
    private const int UnrelatedOrders = 4096;
    private const long AllocationToleranceBytes = 256 * 1024;
    private const long Account = 1000001;

    [Fact]
    public async Task HistoryPreservesRegionsBoundariesAndCommittedChangesAcrossCheckpoint()
    {
        const int remoteRegion = 10000003;
        const int remoteStation = 60000001;
        TestClock clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var pilot = fixture.Store.FindPilot(Account)!;
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Region(Id,Name) VALUES (10000003,'Other region');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES (20000021,10000003,'Other constellation');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30000001,20000021,'Other system');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60000001,TypeId,OwnerId,30000001,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
        }
        long start = clock.UtcNow.UtcDateTime.Date.AddDays(-MarketDefinitions.HistoryDays).ToFileTimeUtc();
        long yesterday = clock.UtcNow.UtcDateTime.Date.AddDays(-1).ToFileTimeUtc();
        long changedTrade;
        using (var transaction = live.Economy.Begin())
        {
            var local = Order(pilot.RegionId, pilot.StationId);
            var remote = Order(remoteRegion, remoteStation);
            changedTrade = Trade(local, yesterday, 2, 100);
            Trade(local, yesterday, 3, 300, isBuy: true);
            Trade(PersonalMarketRules.FirstOrderId + 1, yesterday, 4, 500);
            Trade(PersonalMarketRules.FirstOrderId + 1, yesterday, 4, 500, isBuy: true);
            Trade(remote, yesterday, 1000, 999);
            Trade(PersonalMarketRules.FirstOrderId + 2, yesterday, 1000, 999, station: remoteStation);
            Trade(int.MaxValue, yesterday, 1000, 999);
            Trade(local, start - 1, 1000, 999);
            Trade(local, start, 2, 50);
            Trade(local, clock.Now, 1, 700);
            Trade(local, clock.Now + 1, 1, 900);
            transaction.Commit();

            long Order(int region, int station) =>
                transaction
                    .Add(
                        new NpcMarketOrder
                        {
                            StationId = station,
                            SystemId = pilot.SystemId,
                            RegionId = region,
                            OwnerId = pilot.StationOwnerId,
                            TypeId = RequestedType,
                            PriceCents = 100,
                            Entered = 10,
                            Remaining = 8,
                        }
                    )
                    .Entity.Id;

            long Trade(long order, long time, int quantity, long price, bool isBuy = false, int? station = null) =>
                transaction
                    .Add(
                        new MarketExecution
                        {
                            PilotId = pilot.PilotId,
                            OrderId = order,
                            StationId = station ?? pilot.StationId,
                            CounterpartyId = pilot.StationOwnerId,
                            TypeId = RequestedType,
                            IsBuy = isBuy,
                            Quantity = quantity,
                            PriceCents = price,
                            Time = time,
                        }
                    )
                    .Entity.Id;
        }
        var original = market.History(pilot.PilotId, [RequestedType, RequestedType, int.MaxValue]);
        Assert.Equal(original, market.History(pilot.PilotId, [RequestedType]));
        Assert.Empty(market.History(pilot.PilotId, []));
        Assert.Empty(market.History(pilot.PilotId, [int.MaxValue]));
        var day = Assert.Single(original, d => d.Date == yesterday);
        Assert.Equal(new(RequestedType, yesterday, 100, 500, 3100m / 9, 9, 3), day);
        Assert.Equal(2, Assert.Single(original, d => d.Date == start).Volume);
        Assert.Equal(1, Assert.Single(original, d => d.Date == clock.UtcNow.UtcDateTime.Date.ToFileTimeUtc()).Volume);
        using (var transaction = live.Economy.Begin())
        {
            transaction.Set<MarketExecution>().Find(changedTrade)!.PriceCents = 200;
        }
        Assert.Equal(original, market.History(pilot.PilotId, [RequestedType]));
        using (var transaction = live.Economy.Begin())
        {
            transaction.Set<MarketExecution>().Find(changedTrade)!.PriceCents = 200;
            transaction.Commit();
        }
        Assert.Equal(3300m / 9, Assert.Single(market.History(pilot.PilotId, [RequestedType]), d => d.Date == yesterday).AveragePriceCents);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.Equal(
            2,
            Assert
                .Single(market.History(pilot.PilotId, [RequestedType]), d => d.Date == clock.UtcNow.UtcDateTime.Date.ToFileTimeUtc())
                .Volume
        );
        var expected = market.History(pilot.PilotId, [RequestedType]);
        using (var transaction = live.Economy.Begin())
        {
            transaction.Ships.Find(pilot.ShipId)!.StationId = remoteStation;
            transaction.Commit();
        }
        Assert.Equal(2000, Assert.Single(market.History(pilot.PilotId, [RequestedType]), d => d.Date == yesterday).Volume);
        using (var transaction = live.Economy.Begin())
        {
            transaction.Ships.Find(pilot.ShipId)!.StationId = pilot.StationId;
            transaction.Commit();
        }
        Assert.Equal(expected, market.History(pilot.PilotId, [RequestedType]));
        await live.FlushAsync();
        fixture.Services.Dispose();
        using var reopened = Eden.Server.ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        Assert.Equal(expected, reopened.GetRequiredService<IPilotMarket>().History(pilot.PilotId, [RequestedType]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WarmHistoryRepliesDoNotScaleWithUnrelatedOrdersAndExecutions(bool populated)
    {
        TestClock clock = new(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        using TestWorld fixture = new(clock: clock, liveWorld: true);
        var pilot = fixture.Store.FindPilot(Account)!;
        var live = fixture.Services.GetRequiredService<LiveWorld>();
        if (populated)
        {
            Seed(RequestedType, 1);
        }
        using var scope = fixture.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            Account,
            pilot.Account,
            [pilot],
            p => new(fixture.Store, p, clock, new(), fixture.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var days = fixture.Services.GetRequiredService<IPilotMarket>().History(pilot.PilotId, [RequestedType]);
        Assert.Equal(populated ? 2 : 0, days.Where(d => d.Source == MarketHistorySource.LocalExecution).Sum(d => d.Volume));
        var expected = ReadPair();
        var small = Measure();
        Seed(UnrelatedType, UnrelatedOrders);
        var large = Measure();
        output.WriteLine(
            $"History pairs populated={populated}: small={small.Elapsed.TotalMilliseconds:F2} ms/{small.AllocatedBytes} bytes, "
                + $"large={large.Elapsed.TotalMilliseconds:F2} ms/{large.AllocatedBytes} bytes."
        );
        Assert.InRange(large.AllocatedBytes, 0, small.AllocatedBytes + AllocationToleranceBytes);
        Assert.Empty(context.Notifications);

        byte[][] ReadPair() =>
            new[] { "GetOldPriceHistory", "GetNewPriceHistory" }
                .Select(method =>
                    BlueMarshal.Encode(router.CallWire(context, "marketProxy", method, new object?[] { (long)RequestedType }, _ => { }))
                )
                .ToArray();

        PerformanceSample Measure()
        {
            PerformanceSample sample = default;
            using (SessionPerformance.Capture("four history reply pairs", value => sample = value))
            {
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    var actual = ReadPair();
                    Assert.Equal(expected[0], actual[0]);
                    Assert.Equal(expected[1], actual[1]);
                }
            }
            Assert.Equal(0, sample.Commands);
            Assert.Equal(0, sample.Connections);
            Assert.Equal(0, sample.Fittings);
            return sample;
        }

        void Seed(int type, int count)
        {
            using var transaction = live.Economy.Begin();
            for (int index = 0; index < count; index++)
            {
                var order = transaction
                    .Add(
                        new NpcMarketOrder
                        {
                            StationId = pilot.StationId,
                            SystemId = pilot.SystemId,
                            RegionId = pilot.RegionId,
                            OwnerId = pilot.StationOwnerId,
                            TypeId = type == RequestedType ? type : type + index,
                            PriceCents = 100,
                            Entered = 10,
                            Remaining = 8,
                        }
                    )
                    .Entity;
                transaction.Add(
                    new MarketExecution
                    {
                        PilotId = pilot.PilotId,
                        OrderId = order.Id,
                        StationId = pilot.StationId,
                        CounterpartyId = pilot.StationOwnerId,
                        TypeId = order.TypeId,
                        Quantity = 2,
                        PriceCents = 100,
                        Time = clock.Now,
                    }
                );
            }
            transaction.Commit();
        }
    }
}
