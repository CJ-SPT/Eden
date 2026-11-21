using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Eden.Protocol.Tests.Market;

public sealed class NpcRestingOrderTests(ITestOutputHelper output)
{
    private sealed class Fixture : IDisposable
    {
        internal CapsuleerHaulerTests.Fixture Data { get; } = new(legacyFounders: false);
        internal ServiceProvider Host { get; private set; }
        internal DockedPilot Pilot => Host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        internal IPilotMarket Market => Host.GetRequiredService<IPilotMarket>();
        internal IPersonalMarket Personal => Host.GetRequiredService<IPersonalMarket>();
        internal IMarketDemandMaintenance Demand => Host.GetRequiredService<IMarketDemandMaintenance>();
        internal MarketOrder Bid
        {
            get
            {
                _ = Market.Orders(Pilot.PilotId, 215);
                using WorldDbContext world = new(Data.Database.Path);
                var bid = world
                    .Set<NpcMarketOrder>()
                    .Single(o => o.Kind == NpcMarketKind.Regional && o.Bid && o.TypeId == 215 && o.StationId == Pilot.StationId);
                return new(
                    bid.Id,
                    bid.StationId,
                    bid.SystemId,
                    bid.RegionId,
                    bid.OwnerId,
                    bid.TypeId,
                    true,
                    bid.PriceCents,
                    bid.Remaining,
                    bid.Entered,
                    bid.Issued
                );
            }
        }

        internal Fixture()
        {
            using (CatalogDbContext catalog = new(Data.Database.CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[7,14]}}')"
                );
            }
            Host = ServerServices.Build(Data.Database, Data.Clock, liveWorld: true);
            using WorldDbContext world = new(Data.Database.Path);
            var wallet = world.Set<PilotWallet>().Find(Pilot.PilotId) ?? world.Add(new PilotWallet { PilotId = Pilot.PilotId }).Entity;
            wallet.Cents = 1_000_000_000;
            world.SaveChanges();
        }

        internal long Stack(int quantity = 100)
        {
            using WorldDbContext world = new(Data.Database.Path);
            var item = world
                .Add(
                    new OwnedItem
                    {
                        OwnerId = Pilot.PilotId,
                        TypeId = 215,
                        LocationId = Pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = quantity,
                    }
                )
                .Entity;
            world.SaveChanges();
            return item.Id;
        }

        internal void List(long item, int quantity = 100, long price = 1) =>
            Personal.PlaceSell(Pilot.PilotId, [new(item, Pilot.StationId, 215, price, quantity)], 30);

        internal void Stock(int quantity)
        {
            long id = Bid.Id;
            using WorldDbContext world = new(Data.Database.Path);
            var bid = world.Set<NpcMarketOrder>().Find(id)!;
            bid.Remaining = quantity;
            RegionalMarketStore.Requote(world, bid);
            world.SaveChanges();
        }

        internal async Task Reopen()
        {
            await Host.GetRequiredService<LiveWorld>().FlushAsync();
            Host.Dispose();
            WorldInitialization.Initialize(Data.Database, Data.Clock);
            Host = ServerServices.Build(Data.Database, Data.Clock, liveWorld: true);
        }

        public void Dispose()
        {
            Host.Dispose();
            Data.Dispose();
        }
    }

    [Fact]
    public void PlacementCrossesAtAdvertisedNpcBidAndPaysTaxExactlyOnce()
    {
        using Fixture f = new();
        var bid = f.Bid;
        long before = f.Market.Balance(f.Pilot.PilotId);
        f.List(f.Stack(10), 10);
        var trade = Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
        Assert.Equal(bid.PriceCents, trade.PriceCents);
        Assert.Equal(10, trade.Quantity);
        Assert.True(trade.TaxCents > 0);
        Assert.Equal(
            before - PersonalMarketRules.MinimumBrokerFeeCents + bid.PriceCents * 10 - trade.TaxCents,
            f.Market.Balance(f.Pilot.PilotId)
        );
        Assert.Empty(f.Personal.PersonalOrders(f.Pilot.PilotId));
        Assert.Equal(0, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId, history: true)).Remaining);
        Assert.Equal(bid.Remaining - 10, f.Bid.Remaining);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ResidentSalesKeepUnrelatedRegionalOrdersOutOfMatchingAllocations(bool atSellerStation, bool matchingType)
    {
        using Fixture f = new();
        var pilot = f.Pilot;
        const int type = 215;
        var bid = Assert.Single(f.Market.Orders(pilot.PilotId, type), order => order.Bid && order.StationId == pilot.StationId);
        var live = f.Host.GetRequiredService<LiveWorld>();
        var market = f.Host.GetRequiredService<PilotMarketStore>();
        using CatalogDbContext catalog = new(f.Data.Database.CatalogPath);
        MarketSale[] sale;
        using (var setup = live.Economy.Begin())
        {
            var stack = setup
                .Add(
                    new OwnedItem
                    {
                        Id = setup.NextItemId(),
                        OwnerId = pilot.PilotId,
                        TypeId = type,
                        LocationId = pilot.StationId,
                        FlagId = (int)InventoryFlag.Hangar,
                        Quantity = 10,
                    }
                )
                .Entity;
            sale = [new(stack.Id, pilot.StationId, type, bid.PriceCents, 2)];
            setup.Commit();
        }
        _ = Measure();
        var small = Measure();
        const int depth = 16384;
        int crowdedStation = atSellerStation ? pilot.StationId : pilot.StationId + 1;
        using (var setup = live.Economy.Begin())
        {
            for (int index = 0; index < depth; index++)
            {
                setup.Add(
                    new NpcMarketOrder
                    {
                        StationId = matchingType ? crowdedStation + index : crowdedStation,
                        SystemId = pilot.SystemId,
                        RegionId = pilot.RegionId,
                        OwnerId = pilot.StationOwnerId,
                        TypeId = matchingType ? type : 100_000 + index,
                        Kind = NpcMarketKind.Regional,
                        Bid = true,
                        Entered = 1,
                        Remaining = 1,
                        PriceCents = bid.PriceCents,
                        Issued = bid.Issued,
                    }
                );
            }
            setup.Commit();
        }
        var large = Measure();
        output.WriteLine($"Four sales on the small book: {small.AllocatedBytes:N0} bytes, {small.Elapsed.TotalMilliseconds:F2} ms.");
        output.WriteLine(
            $"Four sales with {depth:N0} unrelated orders at station {crowdedStation}: "
                + $"{large.AllocatedBytes:N0} bytes, {large.Elapsed.TotalMilliseconds:F2} ms."
        );
        output.WriteLine(
            $"SQL commands / opens / fitting calculations per four sales: {large.Commands} / {large.Connections} / {large.Fittings}."
        );
        Assert.Equal((0, 0, 0), (small.Commands, small.Connections, small.Fittings));
        Assert.Equal((0, 0, 0), (large.Commands, large.Connections, large.Fittings));
        Assert.InRange(large.AllocatedBytes, 0, Math.Min(small.AllocatedBytes + 64 * 1024, 512 * 1024));

        PerformanceSample Measure()
        {
            PerformanceSample sample = default;
            using (SessionPerformance.Capture("resident NPC order matching", value => sample = value))
            {
                for (int index = 0; index < 4; index++)
                {
                    // Roll back after each fill so every measurement crosses the same bid for the same quantity.
                    using var world = live.Economy.Begin();
                    var fill = Assert.Single(market.ExecuteSell(world, catalog, pilot.PilotId, sale, 0).Trades);
                    Assert.Equal((bid.Id, bid.PriceCents, 2), (fill.OrderId, fill.PriceCents, fill.Quantity));
                }
            }
            return sample;
        }
    }

    [Fact]
    public async Task PartialSalesRecoverDuringOtherPilotsPlayAndSurviveOfflineRestart()
    {
        using Fixture f = new();
        f.Stock(0);
        f.List(f.Stack());
        f.Stock(3);
        var friend = f.Host.GetRequiredService<SqliteWorld>().FindPilot(1000002)!;
        using (f.Host.GetRequiredService<IHumanPresence>().Select(friend.PilotId, friend.SystemId))
        {
            Assert.Equal(1, f.Demand.AdvanceDemand());
            Assert.Equal(97, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).Remaining);
            Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
            using var idle = SessionPerformance.Begin("unchanged NPC demand", _ => { }, force: true)!;
            for (int i = 0; i < 20; i++)
            {
                Assert.Equal(0, f.Demand.AdvanceDemand());
            }
            Assert.Equal(0, idle.Commands);
            Assert.Equal(0, idle.Connections);
            Assert.Equal(0, idle.FittingCalculations);
        }
        f.Data.Clock.Seconds += 86400;
        await f.Reopen();
        Assert.Equal(0, f.Demand.AdvanceDemand());
        Assert.Equal(97, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).Remaining);
        var active = f.Host.GetRequiredService<SqliteWorld>().FindPilot(1000002)!;
        using var selection = f.Host.GetRequiredService<IHumanPresence>().Select(active.PilotId, active.SystemId);
        f.Data.Clock.Seconds += 600;
        Assert.Equal(1, f.Demand.AdvanceDemand());
        Assert.Empty(f.Personal.PersonalOrders(f.Pilot.PilotId));
        Assert.Equal(100, f.Market.Transactions(f.Pilot.PilotId).Sum(t => t.Quantity));
        Assert.Equal(2, f.Personal.Changes(f.Pilot.PilotId, 0).Count(c => c.Kind == MarketOrderChangeKind.Settled));
    }

    [Fact]
    public void ResidentDemandRecoveryKeepsUnrelatedStockOutOfDeadlinePlanning()
    {
        const int unrelatedOrders = 4096;
        using Fixture f = new();
        f.Stock(0);
        f.List(f.Stack(5), 5, 100_000);
        var live = f.Host.GetRequiredService<LiveWorld>();
        var signal = f.Host.GetRequiredService<MarketChangeSignal>();
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        f.Data.Clock.Seconds += 1;
        Assert.Equal(0, f.Demand.AdvanceDemand());
        long deadline = signal.DemandDeadline;
        Assert.Equal(RegionalMarketRules.RecoveryIntervalTicks, deadline);
        var small = Measure();

        using (var world = live.Economy.Begin())
        {
            for (int index = 0; index < unrelatedOrders; index++)
            {
                var order = world
                    .Add(
                        new NpcMarketOrder
                        {
                            StationId = f.Pilot.StationId,
                            SystemId = f.Pilot.SystemId,
                            RegionId = f.Pilot.RegionId,
                            OwnerId = f.Pilot.StationOwnerId,
                            TypeId = 100_000 + index,
                            Kind = NpcMarketKind.Regional,
                            Bid = true,
                            Entered = 1,
                            Remaining = 1,
                            PriceCents = 100,
                            Issued = 1,
                        }
                    )
                    .Entity;
                world.Add(new SavedRegionalMarketStock { OrderId = order.Id, AnchorCents = 100 });
            }
            world.Commit();
        }
        var large = Measure();
        output.WriteLine(
            $"Four demand passes with one open listing: {small.Elapsed.TotalMilliseconds:F2} ms / {small.AllocatedBytes:N0} bytes. "
                + $"With {unrelatedOrders:N0} unrelated bids: {large.Elapsed.TotalMilliseconds:F2} ms / {large.AllocatedBytes:N0} bytes."
        );
        Assert.InRange(large.AllocatedBytes, 0, small.AllocatedBytes + 8 * 1024 * 1024);
        Assert.Equal(5, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).Remaining);

        PerformanceSample Measure()
        {
            PerformanceSample sample = default;
            using (SessionPerformance.Capture("resident demand recovery", value => sample = value))
            {
                for (int repeat = 0; repeat < 4; repeat++)
                {
                    signal.ScheduleDemand(0, 0);
                    Assert.Equal(0, f.Demand.AdvanceDemand());
                    Assert.Equal(deadline, signal.DemandDeadline);
                    Assert.Equal(0, signal.DemandCursor);
                }
            }
            Assert.Equal(0, sample.Commands);
            Assert.Equal(0, sample.Connections);
            Assert.Equal(0, sample.Fittings);
            return sample;
        }
    }

    [Fact]
    public async Task ResidentDemandDoesNotVisitUnrelatedSimulatedSystems()
    {
        using Fixture f = new();
        f.Stock(0);
        f.List(f.Stack(5), 5);
        var live = f.Host.GetRequiredService<LiveWorld>();
        var signal = f.Host.GetRequiredService<MarketChangeSignal>();
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        var demand = f.Demand;
        Assert.Equal(0, demand.AdvanceDemand());
        var small = Measure();
        const int remote = 31000001;
        for (int system = remote; system < remote + 256; system++)
        {
            _ = live.CombatForSystem(system);
        }
        var large = Measure();
        output.WriteLine(
            $"Eight demand polls before and after loading 256 unrelated systems: "
                + $"{small.AllocatedBytes:N0} / {large.AllocatedBytes:N0} bytes, "
                + $"{small.Elapsed.TotalMilliseconds:F2} / {large.Elapsed.TotalMilliseconds:F2} ms."
        );
        Assert.InRange(large.AllocatedBytes, 0, small.AllocatedBytes + 256 * 1024);

        f.Stock(3);
        signal.ScheduleDemand(0, 0);
        using ManualResetEventSlim entered = new();
        using ManualResetEventSlim release = new();
        var holding = Task.Run(() =>
        {
            using var lease = live.Enter("paused unrelated bot system", remote);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, await Task.Run(() => demand.AdvanceDemand()).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            release.Set();
            await holding;
        }
        Assert.Equal(2, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).Remaining);
        Assert.Equal(3, Assert.Single(f.Market.Transactions(f.Pilot.PilotId)).Quantity);

        PerformanceSample Measure()
        {
            PerformanceSample sample = default;
            using (SessionPerformance.Capture("warm demand polls", value => sample = value))
            {
                for (int repeat = 0; repeat < 8; repeat++)
                {
                    signal.ScheduleDemand(0, 0);
                    Assert.Equal(0, demand.AdvanceDemand());
                }
            }
            Assert.Equal((0, 0, 0), (sample.Commands, sample.Connections, sample.Fittings));
            return sample;
        }
    }

    [Fact]
    public void ResidentDemandYieldsBetweenLocationsAndResumesBeforeADueSale()
    {
        const int earlierListings = 24;
        using Fixture f = new();
        f.Stock(0);
        long earlierSeller = f.Host.GetRequiredService<SqliteWorld>().FindPilot(1000002)!.PilotId;
        var live = f.Host.GetRequiredService<LiveWorld>();
        var signal = f.Host.GetRequiredService<MarketChangeSignal>();
        using (var world = live.Economy.Begin())
        {
            for (int index = 1; index <= earlierListings; index++)
            {
                world.Add(
                    new SavedPersonalOrder
                    {
                        Id = PersonalMarketRules.FirstOrderId + index,
                        PilotId = earlierSeller,
                        StationId = f.Pilot.StationId,
                        SystemId = f.Pilot.SystemId,
                        RegionId = f.Pilot.RegionId,
                        TypeId = 100_000 + index,
                        PriceCents = 1,
                        Remaining = 1,
                        Entered = 1,
                        Issued = f.Data.Clock.Now,
                        Modified = f.Data.Clock.Now,
                        Expires = f.Data.Clock.Now + TimeSpan.FromDays(30).Ticks,
                        DurationDays = 30,
                    }
                );
            }
            world.Commit();
        }
        f.List(f.Stack(1), 1);
        f.Stock(1);
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        Assert.Equal(0, f.Demand.AdvanceDemand());
        Assert.InRange(signal.DemandCursor, PersonalMarketRules.FirstOrderId + 1, PersonalMarketRules.FirstOrderId + 8);
        Assert.Empty(f.Market.Transactions(f.Pilot.PilotId));
        int settled = 0;
        for (int turn = 0; turn <= earlierListings && signal.DemandCursor != 0; turn++)
        {
            long previous = signal.DemandCursor;
            settled += f.Demand.AdvanceDemand();
            Assert.True(signal.DemandCursor == 0 || signal.DemandCursor > previous);
        }
        Assert.Equal(1, settled);
        Assert.Equal(0, signal.DemandCursor);
        Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
        Assert.Empty(f.Personal.PersonalOrders(f.Pilot.PilotId));
        Assert.Equal(earlierListings, f.Personal.PersonalOrders(earlierSeller).Count);
    }

    [Fact]
    public void MaintenanceUsesPricePriorityAndRejectsDuplicateConsumptionAfterRollback()
    {
        using Fixture f = new();
        f.Stock(0);
        f.List(f.Stack(4), 4, 10);
        f.List(f.Stack(4), 4, 5);
        var orders = f.Personal.PersonalOrders(f.Pilot.PilotId);
        long cheaper = orders.Single(o => o.PriceCents == 5).Id;
        f.Stock(4);
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        long before = f.Market.Balance(f.Pilot.PilotId);
        var live = f.Host.GetRequiredService<LiveWorld>();
        long seller = f.Pilot.PilotId;
        SetBalance(long.MaxValue);
        Assert.Throws<OverflowException>(() => f.Demand.AdvanceDemand());
        Assert.Equal(long.MaxValue, f.Market.Balance(seller));
        Assert.Equal(4, f.Bid.Remaining);
        Assert.Equal(2, f.Personal.PersonalOrders(f.Pilot.PilotId).Count);
        Assert.Empty(f.Market.Transactions(f.Pilot.PilotId));
        SetBalance(before);
        Assert.Equal(1, f.Demand.AdvanceDemand(1));
        Assert.Equal(cheaper, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId, history: true)).Id);
        Assert.Equal(10, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).PriceCents);
        Assert.Equal(0, f.Bid.Remaining);
        Assert.Equal(0, f.Demand.AdvanceDemand());
        Assert.Single(f.Market.Transactions(f.Pilot.PilotId));

        void SetBalance(long cents)
        {
            using var world = live.Economy.Begin(new(pilots: [seller]));
            world.Set<PilotWallet>().Find(seller)!.Cents = cents;
            world.Commit();
        }
    }

    [Fact]
    public void ModifiedListingsFillAndExpiredOrCancelledListingsReturnTheirEscrow()
    {
        using Fixture f = new();
        f.Stock(0);
        f.List(f.Stack(5), 5, 1000);
        var order = Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId));
        f.Data.Clock.Seconds += 300;
        f.Stock(5);
        f.Personal.Modify(
            f.Pilot.PilotId,
            new(order.Id, 1, false, order.StationId, order.SystemId, order.PriceCents, order.Remaining, order.Issued)
        );
        Assert.Empty(f.Personal.PersonalOrders(f.Pilot.PilotId));
        Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
        f.Stock(0);
        f.List(f.Stack(5), 5);
        var cancelled = Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId));
        f.Personal.Cancel(f.Pilot.PilotId, cancelled.Id, cancelled.RegionId);
        f.List(f.Stack(5), 5);
        f.Data.Clock.Seconds += 31 * 86400;
        Assert.Equal(1, f.Personal.Expire());
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        Assert.Equal(0, f.Demand.AdvanceDemand());
        Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
        using WorldDbContext world = new(f.Data.Database.Path);
        Assert.Equal(10, world.Set<OwnedItem>().Where(i => i.OwnerId == f.Pilot.PilotId && i.TypeId == 215).Sum(i => i.Quantity));
    }

    [Fact]
    public void BoundedMaintenanceAdvancesPastUnfillablePages()
    {
        using Fixture f = new();
        using (WorldDbContext world = new(f.Data.Database.Path))
        {
            for (int i = 1; i <= 101; i++)
            {
                world.Add(
                    new SavedPersonalOrder
                    {
                        Id = PersonalMarketRules.FirstOrderId + i,
                        PilotId = f.Pilot.PilotId,
                        StationId = f.Pilot.StationId,
                        SystemId = f.Pilot.SystemId,
                        RegionId = f.Pilot.RegionId,
                        TypeId = i == 101 ? 215 : 1,
                        PriceCents = 1,
                        Remaining = 1,
                        Entered = 1,
                        Issued = f.Data.Clock.Now,
                        Modified = f.Data.Clock.Now,
                        Expires = f.Data.Clock.Now + TimeSpan.FromDays(30).Ticks,
                        DurationDays = 30,
                    }
                );
            }
            world.SaveChanges();
        }
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        Assert.Equal(0, f.Demand.AdvanceDemand());
        Assert.Equal(1, f.Demand.AdvanceDemand());
        Assert.Equal(100, f.Personal.PersonalOrders(f.Pilot.PilotId).Count);
        Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
    }

    [Fact]
    public async Task ConcurrentImmediateSaleCannotJumpAQueuedListingOrReuseItsLastUnit()
    {
        using Fixture f = new();
        f.Stock(0);
        f.List(f.Stack(1), 1);
        long waiting = Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).Id;
        long incoming = f.Stack(1);
        f.Stock(1);
        using var presence = f.Host.GetRequiredService<IHumanPresence>().Select(f.Pilot.PilotId, f.Pilot.SystemId);
        var sale = Task.Run(() =>
        {
            Assert.Throws<InvalidDataException>(() => f.Market.Sell(f.Pilot.PilotId, [new(incoming, f.Pilot.StationId, 215, 1, 1)]));
        });
        var maintenance = Task.Run(() => f.Demand.AdvanceDemand());
        await Task.WhenAll(sale, maintenance);
        Assert.Equal(1, await maintenance);
        Assert.Equal(waiting, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId, history: true)).Id);
        Assert.Equal(1, Assert.Single(f.Market.Transactions(f.Pilot.PilotId)).Quantity);
        Assert.Equal(0, f.Bid.Remaining);
        using WorldDbContext world = new(f.Data.Database.Path);
        Assert.Equal(1, world.Set<OwnedItem>().Find(incoming)!.Quantity);
    }
}
