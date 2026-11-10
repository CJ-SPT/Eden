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

namespace Eden.Protocol.Tests.Market;

public sealed class NpcRestingOrderTests
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
        using (WorldDbContext world = new(f.Data.Database.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectDemand BEFORE INSERT ON WorldMarketTrade BEGIN SELECT RAISE(ABORT,'settlement failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => f.Demand.AdvanceDemand());
        Assert.Equal(before, f.Market.Balance(f.Pilot.PilotId));
        Assert.Equal(4, f.Bid.Remaining);
        Assert.Equal(2, f.Personal.PersonalOrders(f.Pilot.PilotId).Count);
        Assert.Empty(f.Market.Transactions(f.Pilot.PilotId));
        using (WorldDbContext world = new(f.Data.Database.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectDemand");
        }
        Assert.Equal(1, f.Demand.AdvanceDemand(1));
        Assert.Equal(cheaper, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId, history: true)).Id);
        Assert.Equal(10, Assert.Single(f.Personal.PersonalOrders(f.Pilot.PilotId)).PriceCents);
        Assert.Equal(0, f.Bid.Remaining);
        Assert.Equal(0, f.Demand.AdvanceDemand());
        Assert.Single(f.Market.Transactions(f.Pilot.PilotId));
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
