using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class RemoteOrderManagementTests
{
    private const long SellerAccount = 1000001;
    private const long BuyerAccount = 1000002;
    private const int NeighborSystem = 30000144;
    private const int DisconnectedSystem = 30009998;
    private const int ForeignSystem = 30009999;

    [Fact]
    public void NativeModificationUsesEffectiveDaytradingAndCurrentSystem()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        AddSystems(fixture);
        fixture.List();
        var pilot = fixture.Pilot;
        using CourierServiceTests.Client client = new(fixture.Db, fixture.Clock, SellerAccount);
        int ordinal = 0;

        void TryModify(string? error)
        {
            client.Refresh();
            var before = fixture.Order();
            long balance = fixture.Market.Balance(pilot.PilotId);
            long newPrice = 1100 + 100 * ordinal++;
            var reply = client.Call(
                "ModifyCharOrder",
                [
                    before.Id,
                    newPrice / 100d,
                    before.Bid,
                    (long)before.StationId,
                    (long)before.SystemId,
                    before.PriceCents / 100d,
                    (long)before.Range,
                    (long)before.Remaining,
                    before.Issued,
                ],
                "marketProxy"
            );
            if (error is not null)
            {
                Assert.Contains(error, Assert.IsType<PilotCallResult.Failure>(reply).Message);
                Assert.Equal(before, fixture.Order());
                Assert.Equal(balance, fixture.Market.Balance(pilot.PilotId));
                return;
            }
            Assert.Same(PilotCallResult.None, reply);
            Assert.Equal(before with { PriceCents = newPrice }, fixture.Order());
            Assert.True(balance > fixture.Market.Balance(pilot.PilotId));
            Assert.Contains(client.Context.Notifications.OfType<OwnOrdersChanged>(), change => change.Reason == "Modify");
            fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        }

        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, pilot.SystemId);
        TryModify("Daytrading");
        Train(fixture, AccountCloneGrade.Alpha, level: 1);
        TryModify("Daytrading");
        Train(fixture, AccountCloneGrade.Omega, level: 1);
        TryModify(error: null);

        fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, NeighborSystem);
        TryModify("Daytrading");
        Train(fixture, AccountCloneGrade.Omega, level: 2);
        TryModify(error: null);

        fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, DisconnectedSystem);
        Train(fixture, AccountCloneGrade.Omega, level: 4);
        TryModify("Daytrading");
        Train(fixture, AccountCloneGrade.Omega, level: 5);
        TryModify(error: null);

        fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, ForeignSystem);
        TryModify("market region");
    }

    [Fact]
    public async Task NativeCancellationFromAnotherRegionRestoresInventoryAndEscrowOnce()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        AddSystems(fixture);
        var seller = fixture.Pilot;
        var buyer = fixture.Db.Store.FindPilot(BuyerAccount)!;
        fixture.List(quantity: 20);
        var sell = fixture.Order();
        fixture.Market.PlaceBuy(buyer.PilotId, new(sell.StationId, sell.TypeId, 500, 5, 1));
        var buy = fixture.Order(buyer.PilotId);
        Assert.Equal(2500, buy.EscrowCents);
        long buyerAfterPlacement = fixture.Market.Balance(buyer.PilotId);
        fixture.Db.Store.SetPosition(seller.ShipId, SpacePosition.Zero, ForeignSystem);
        fixture.Db.Store.SetPosition(buyer.ShipId, SpacePosition.Zero, ForeignSystem);
        using (CourierServiceTests.Client client = new(fixture.Db, fixture.Clock, SellerAccount))
        {
            client.Refresh();
            Assert.IsType<PilotCallResult.Failure>(client.Call("CancelCharOrder", [buy.Id, (long)buy.RegionId], "marketProxy"));
            Assert.IsType<PilotCallResult.Failure>(client.Call("CancelCharOrder", [sell.Id, 1L], "marketProxy"));
            Assert.Equal(sell, fixture.Order());
            Assert.Same(PilotCallResult.None, client.Call("CancelCharOrder", [sell.Id, (long)sell.RegionId], "marketProxy"));
            Assert.IsType<PilotCallResult.Failure>(client.Call("CancelCharOrder", [sell.Id, (long)sell.RegionId], "marketProxy"));
            Assert.Contains(client.Context.Notifications.OfType<OwnOrdersChanged>(), change => change.Reason == "Expiry");
        }

        fixture.Market.Cancel(buyer.PilotId, buy.Id, buy.RegionId);
        var live = fixture.Db.Services.GetRequiredService<LiveWorld>();
        await live.FlushAsync();
        fixture.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var market = reopened.GetRequiredService<IPersonalMarket>();
        Assert.Empty(market.PersonalOrders(seller.PilotId));
        Assert.Empty(market.PersonalOrders(buyer.PilotId));
        Assert.Equal(0, market.Escrow(buyer.PilotId).IskCents);
        Assert.Equal(buyerAfterPlacement + buy.EscrowCents, reopened.GetRequiredService<IPilotWalletReader>().Balance(buyer.PilotId));
        var returned = reopened
            .GetRequiredService<LiveWorld>()
            .Economy.Inspect(world =>
                world
                    .Filter<OwnedItem>(item =>
                        item.OwnerId == seller.PilotId
                        && item.LocationId == sell.StationId
                        && item.FlagId == (int)InventoryFlag.Hangar
                        && item.TypeId == sell.TypeId
                    )
                    .Sum(item => item.Quantity)
            );
        Assert.Equal(100, returned);
    }

    private static void Train(PersonalMarketTests.Fixture fixture, AccountCloneGrade grade, int level)
    {
        int skill = (int)MarketSkill.Daytrading;
        long pilot = fixture.Pilot.PilotId;
        using var world = fixture.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin(new(pilots: [pilot]));
        world.Set<Account>().Find(SellerAccount)!.CloneGrade = grade;
        var trained = world.Set<PilotSkill>().Find(pilot, skill) ?? world.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
        trained.Points = TrainedSkill.PointsForLevel(1, level);
        world.Commit();
    }

    private static void AddSystems(PersonalMarketTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Region(Id,Name) VALUES (10000999,'Other region');
            INSERT INTO Constellation(Id,RegionId,Name) VALUES (20009999,10000999,'Other constellation');
            INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES
                (30009998,20000020,'Disconnected system'),(30009999,20009999,'Other system');
            """
        );
    }
}
