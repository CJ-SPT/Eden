using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
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

    [Fact]
    public void ProcurementPlacesNativeBuyOrderAndDeliversAtTheOriginStation()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        AddSystems(fixture);
        var buyer = fixture.Db.Store.FindPilot(BuyerAccount)!;
        var seller = fixture.Pilot;
        fixture.Db.Store.SetPosition(buyer.ShipId, SpacePosition.Zero, NeighborSystem);
        using CourierServiceTests.Client client = new(fixture.Db, fixture.Clock, BuyerAccount);
        client.Refresh();
        const int ammo = 215;
        object?[] arguments = [(long)seller.StationId, (long)ammo, 10d, 5L, -1L, 1L, 1L, false, null];
        PilotCallResult Place() => client.Call("PlaceBuyOrder", arguments, "marketProxy");
        long startingBalance = fixture.Market.Balance(buyer.PilotId);
        Assert.Contains("Procurement", Assert.IsType<PilotCallResult.Failure>(Place()).Message);
        Train(fixture, AccountCloneGrade.Omega, 1, MarketSkill.Procurement, BuyerAccount);
        Assert.Contains("Procurement", Assert.IsType<PilotCallResult.Failure>(Place()).Message);
        Train(fixture, AccountCloneGrade.Alpha, 5, MarketSkill.Procurement, BuyerAccount);
        Assert.Contains("Procurement", Assert.IsType<PilotCallResult.Failure>(Place()).Message);
        Assert.Empty(fixture.Market.PersonalOrders(buyer.PilotId));
        Assert.Equal(startingBalance, fixture.Market.Balance(buyer.PilotId));

        Train(fixture, AccountCloneGrade.Omega, 2, MarketSkill.Procurement, BuyerAccount);
        Assert.Same(PilotCallResult.None, Place());
        var order = fixture.Order(buyer.PilotId);
        Assert.Equal(seller.StationId, order.StationId);
        Assert.Equal(5000, order.EscrowCents);
        Assert.Contains(client.Context.Notifications.OfType<OwnOrdersChanged>(), change => change.Reason == "Add");
        fixture.List(quantity: 5, price: 900);
        Assert.Empty(fixture.Market.PersonalOrders(buyer.PilotId));
        var received = fixture
            .Db.Services.GetRequiredService<LiveWorld>()
            .Economy.Inspect(world => world.Filter<OwnedItem>(item => item.OwnerId == buyer.PilotId && item.TypeId == ammo).Single());
        Assert.Equal(seller.StationId, received.LocationId);
        Assert.Equal(5, received.Quantity);

        Train(fixture, AccountCloneGrade.Omega, 5, MarketSkill.Procurement, BuyerAccount);
        fixture.Db.Store.SetPosition(buyer.ShipId, SpacePosition.Zero, ForeignSystem);
        client.Refresh();
        Assert.Contains("market region", Assert.IsType<PilotCallResult.Failure>(Place()).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void MarketingSellsOnlyOwnedHangarStockAtTheRemoteStation(int days)
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        AddSystems(fixture);
        var pilot = fixture.Pilot;
        var buyer = fixture.Db.Store.FindPilot(BuyerAccount)!;
        var live = fixture.Db.Services.GetRequiredService<LiveWorld>();
        const int ammo = 215;
        long itemId = live.Economy.Inspect(world =>
            world.Filter<OwnedItem>(item => item.OwnerId == pilot.PilotId && item.TypeId == ammo).Single().Id
        );
        using CourierServiceTests.Client client = new(fixture.Db, fixture.Clock, SellerAccount);
        void Move(int system)
        {
            fixture.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, system);
            client.Refresh();
        }
        PilotCallResult Sell() =>
            client.Call(
                "PlaceMultiSellOrder",
                [
                    new List<object?>
                    {
                        new BlueInstance(
                            "utillib.KeyVal",
                            new Dictionary<string, object?>
                            {
                                ["itemID"] = itemId,
                                ["stationID"] = (long)pilot.StationId,
                                ["typeID"] = (long)ammo,
                                ["quantity"] = 5L,
                                ["price"] = 9d,
                            }
                        ),
                    },
                    false,
                    (long)days,
                    null,
                ],
                "marketProxy"
            );

        if (days == 0)
        {
            fixture.Market.PlaceBuy(buyer.PilotId, new(pilot.StationId, ammo, 1000, 5, 1));
        }
        Move(NeighborSystem);
        Assert.Contains("Marketing", Assert.IsType<PilotCallResult.Failure>(Sell()).Message);
        Train(fixture, AccountCloneGrade.Alpha, 5, MarketSkill.Marketing);
        Move(DisconnectedSystem);
        Assert.Contains("Marketing", Assert.IsType<PilotCallResult.Failure>(Sell()).Message);
        Move(NeighborSystem);
        using (var action = live.Economy.Begin())
        {
            var stack = action.Set<OwnedItem>().Find(itemId)!;
            stack.LocationId = pilot.ShipId;
            stack.FlagId = (int)InventoryFlag.Cargo;
            action.Commit();
        }
        Assert.Contains("unavailable", Assert.IsType<PilotCallResult.Failure>(Sell()).Message);
        Assert.Equal(100, fixture.Quantity(pilot.PilotId));
        using (var action = live.Economy.Begin())
        {
            var stack = action.Set<OwnedItem>().Find(itemId)!;
            stack.LocationId = pilot.StationId;
            stack.FlagId = (int)InventoryFlag.Hangar;
            action.Commit();
        }
        Assert.IsType<PilotCallResult.Success>(Sell());
        Assert.Equal(95, fixture.Quantity(pilot.PilotId));
        if (days == 0)
        {
            Assert.Equal(pilot.StationId, Assert.Single(fixture.Market.Transactions(pilot.PilotId)).StationId);
        }
        else
        {
            Assert.Equal(pilot.StationId, fixture.Order().StationId);
        }
    }

    private static void Train(
        PersonalMarketTests.Fixture fixture,
        AccountCloneGrade grade,
        int level,
        MarketSkill marketSkill = MarketSkill.Daytrading,
        long accountId = SellerAccount
    )
    {
        int skill = (int)marketSkill;
        long pilot = fixture.Db.Store.FindPilot(accountId)!.PilotId;
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        double rank = catalog.RawFittingAttributes(skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
        using var world = fixture.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin(new(pilots: [pilot]));
        world.Set<Account>().Find(accountId)!.CloneGrade = grade;
        var trained = world.Set<PilotSkill>().Find(pilot, skill) ?? world.Add(new PilotSkill { PilotId = pilot, TypeId = skill }).Entity;
        trained.Points = TrainedSkill.PointsForLevel(rank, level);
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
