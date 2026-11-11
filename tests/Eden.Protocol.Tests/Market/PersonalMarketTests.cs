using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Docking;
using Eden.Protocol.Flight;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.Server.Maintenance;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class PersonalMarketTests
{
    private const int Ammo = 215;
    private const long Seller = 90000001;
    private const long Buyer = 90000002;
    private const long Stack = InventoryDefinitions.FirstOwnedItemId + 100;
    private const long Budget = 10_000_000;

    internal sealed class Fixture : IDisposable
    {
        internal TestClock Clock { get; } = new();
        internal TestWorld Db { get; }
        internal PilotMarketStore Market { get; }
        internal DockedPilot Pilot => Db.Store.FindPilot(1000001)!;

        internal Fixture(bool live = false)
        {
            Db = new(clock: Clock, liveWorld: live);
            Market = new(new(Db.Path), Clock, Db.Services.GetRequiredService<MarketChangeSignal>());
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemCategory VALUES (8,'Charge');
                    INSERT INTO ItemGroup VALUES (85,'Hybrid Charge',8);
                    INSERT INTO ItemType(Id,Name,GroupId) VALUES (215,'Iron Charge S',85);
                    INSERT INTO SourceRecord VALUES ('client/types','215','{{"published":1,"marketGroupID":1,"volume":0.0025}}');
                    INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[5,7,14,17]}}');
                    """
                );
            }
            using WorldDbContext world = new(Db.Path);
            world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
            world.Set<SavedWorldEconomy>().Single().TestingGrants = false;
            foreach (var id in new[] { Seller, Buyer })
            {
                world.Add(new PilotWallet { PilotId = id, Cents = Budget });
            }
            world.Ships.Single(s => s.OwnerId == Buyer).StationId = Pilot.StationId;
            world.Add(
                new OwnedItem
                {
                    Id = Stack,
                    OwnerId = Seller,
                    TypeId = Ammo,
                    LocationId = Pilot.StationId,
                    FlagId = (int)InventoryFlag.Hangar,
                    Quantity = 100,
                }
            );
            world.SaveChanges();
        }

        internal void List(int quantity = 100, long price = 1000, int days = 1) =>
            Market.PlaceSell(Seller, [new(Stack, Pilot.StationId, Ammo, price, quantity)], days);

        internal MarketOrder Order(long pilot = Seller) => Assert.Single(Market.PersonalOrders(pilot));

        internal MarketResult Buy(int quantity, long price = 1000) => Market.Buy(Buyer, Pilot.StationId, [new(Ammo, price, quantity)]);

        internal int Quantity(long pilot)
        {
            using WorldDbContext world = new(Db.Path);
            return world.Set<OwnedItem>().Where(i => i.OwnerId == pilot && i.TypeId == Ammo).Sum(i => i.Quantity);
        }

        internal PersonalMarketModification Modification(MarketOrder order, long price) =>
            new(order.Id, price, order.Bid, order.StationId, order.SystemId, order.PriceCents, order.Remaining, order.Issued);

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemoteBuyerFillsPlayerListingAndGoodsRemainAtSellerStation(bool inSpace)
    {
        using Fixture f = new();
        f.List();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60003761,TypeId,OwnerId,30000144,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
        }
        long shipId;
        using (WorldDbContext world = new(f.Db.Path))
        {
            var ship = world.Ships.Single(s => s.OwnerId == Buyer);
            shipId = ship.Id;
            ship.StationId = 60003761;
            world.SaveChanges();
        }
        if (inSpace)
        {
            f.Db.Store.SetPosition(shipId, Eden.Gameplay.Flight.SpacePosition.Zero, 30000144);
        }
        long sellerBefore = f.Market.Balance(Seller);
        var purchase = f.Buy(40);
        Assert.Equal(Budget - 40000, purchase.BalanceCents);
        Assert.Equal(sellerBefore + 37000, f.Market.Balance(Seller));
        Assert.Equal(60, f.Order().Remaining);
        Assert.Equal(f.Pilot.StationId, Assert.Single(purchase.Changes).Item.LocationId);
        Assert.Equal(40, Assert.Single(purchase.Changes).Item.Quantity);
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        var goods = Assert.Single(saved.Set<OwnedItem>(), i => i.OwnerId == Buyer && i.TypeId == Ammo);
        Assert.Equal(f.Pilot.StationId, goods.LocationId);
        Assert.Equal((int)InventoryFlag.Hangar, goods.FlagId);
        Assert.Equal(40, goods.Quantity);
        Assert.Equal(60, f.Order().Remaining);
        Assert.Equal(Budget - 40000, f.Market.Balance(Buyer));
        Assert.Single(f.Market.Transactions(Buyer));
        Assert.Single(f.Market.Transactions(Seller));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OfflineSellerReceivesPartialFillsAndHistoryCountsEachTradeOnceAcrossCheckpoint(bool live)
    {
        using Fixture f = new(live);
        f.List();
        Assert.Equal(0, f.Quantity(Seller));
        Assert.Equal(new MarketEscrow(0, 100_000), f.Market.Escrow(Seller));
        long orderId = f.Order().Id;
        var result = f.Buy(40, 1200);
        Assert.Equal(48_000, Assert.Single(result.Trades).PriceCents * 40);
        Assert.Equal(40, f.Quantity(Buyer));
        Assert.Equal(60, f.Order().Remaining);
        Assert.Equal(Budget - 10_000 + 44_400, f.Market.Balance(Seller));
        Assert.Equal(Budget - 48_000, f.Market.Balance(Buyer));
        Assert.Single(f.Market.Transactions(Seller));
        long buyerDay = DateTime.FromFileTimeUtc(f.Market.Transactions(Buyer).Last().Time).Date.ToFileTimeUtc();
        Assert.Equal(40, Assert.Single(f.Market.History(Buyer, [Ammo]), day => day.Date == buyerDay).Volume);
        if (live)
        {
            var runtime = f.Db.Services.GetRequiredService<LiveWorld>();
            await runtime.FlushAsync();
            using var disk = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={f.Db.Path};Mode=ReadOnly;Pooling=False");
            disk.Open();
            using var query = disk.CreateCommand();
            query.CommandText = "SELECT Remaining FROM WorldPersonalOrder";
            Assert.Equal(60L, query.ExecuteScalar());
        }
        else
        {
            WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        }
        Assert.Equal(orderId, f.Order().Id);
        f.Market.Cancel(Seller, orderId, f.Pilot.RegionId);
        Assert.Equal(60, f.Quantity(Seller));
        Assert.Empty(f.Market.PersonalOrders(Seller));
        Assert.Throws<InvalidDataException>(() => f.Market.Cancel(Seller, orderId, f.Pilot.RegionId));
        Assert.Equal(60, f.Quantity(Seller));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullyFundedBuyOrdersRefundPriceImprovementAndDoNotChargeTheWalletTwice(bool live)
    {
        using Fixture f = new(live);
        f.Market.PlaceBuy(Buyer, new(f.Pilot.StationId, Ammo, 2000, 60, 1));
        Assert.Equal(Budget - 120_000 - 10_000, f.Market.Balance(Buyer));
        Assert.Equal(120_000, f.Market.Escrow(Buyer).IskCents);
        f.Market.Sell(Seller, [new(Stack, f.Pilot.StationId, Ammo, 1500, 40)]);
        Assert.Equal(40, f.Quantity(Buyer));
        Assert.Equal(40_000, f.Market.Escrow(Buyer).IskCents);
        Assert.Equal(Budget - 110_000, f.Market.Balance(Buyer));
        Assert.Equal(0, Assert.Single(f.Market.Transactions(Buyer)).WalletAmountCents);
        f.Market.Cancel(Buyer, f.Order(Buyer).Id, f.Pilot.RegionId);
        Assert.Equal(Budget - 70_000, f.Market.Balance(Buyer));
        var journal = f.Db.Services.GetRequiredService<IPilotWalletReader>().Journal(Buyer);
        Assert.Equal(-70_000, journal.MarketPayments!.Sum(p => p.AmountCents));
        long sellerDay = DateTime.FromFileTimeUtc(f.Market.Transactions(Seller).Last().Time).Date.ToFileTimeUtc();
        Assert.Equal(40, Assert.Single(f.Market.History(Seller, [Ammo]), day => day.Date == sellerDay).Volume);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MatchingUsesPriceThenTimeAndModificationRematchesWithEscrowAndFeeRollback(bool live)
    {
        using Fixture f = new(live);
        f.List(20, 900);
        long first = f.Order().Id;
        f.List(30, 800);
        var result = f.Buy(40, 1000);
        Assert.Equal(new[] { 30, 10 }, result.Trades.Select(t => t.Quantity));
        Assert.Equal(first, result.Trades[1].OrderId);
        var order = f.Order();
        Assert.Throws<InvalidDataException>(() => f.Market.Modify(Seller, f.Modification(order, 1100)));
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        long before = f.Market.Balance(Seller);
        f.Market.Modify(Seller, f.Modification(order, 1100));
        Assert.Equal(1100, f.Order().PriceCents);
        Assert.Equal(before - 10_000, f.Market.Balance(Seller));
        Assert.Throws<InvalidDataException>(() => f.Market.Modify(Seller, f.Modification(order, 1100)));
        Assert.Equal(before - 10_000, f.Market.Balance(Seller));
        f.Market.PlaceBuy(Buyer, new(f.Pilot.StationId, Ammo, 1000, 10, 1));
        var buy = f.Order(Buyer);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        f.Market.Modify(Buyer, f.Modification(buy, 1200));
        Assert.Empty(f.Market.PersonalOrders(Buyer));
        Assert.Empty(f.Market.PersonalOrders(Seller));
        Assert.Equal(50, f.Quantity(Buyer));
        Assert.Equal(0, f.Market.Escrow(Buyer).IskCents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompetingBuyersCannotDuplicateListedGoodsOrOverdrawEscrow(bool live)
    {
        using Fixture f = new(live);
        f.List(10);
        async Task<bool> Attempt() =>
            await Task.Run(() =>
            {
                try
                {
                    f.Buy(10);
                    return true;
                }
                catch (InvalidDataException)
                {
                    return false;
                }
            });
        Assert.Equal(1, (await Task.WhenAll(Attempt(), Attempt())).Count(success => success));
        Assert.Equal(10, f.Quantity(Buyer));
        Assert.Equal(Budget - 10_000, f.Market.Balance(Buyer));
        Assert.Single(f.Market.Transactions(Seller));
    }

    [Theory]
    [InlineData(0, 1000, 1)]
    [InlineData(-1, 1000, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1000, 2)]
    [InlineData(101, 1000, 1)]
    public void RejectedListingsLeaveInventoryWalletAndOrdersUnchanged(int quantity, long price, int days)
    {
        using Fixture f = new();
        Assert.Throws<InvalidDataException>(() => f.List(quantity, price, days));
        Assert.Equal(100, f.Quantity(Seller));
        Assert.Equal(Budget, f.Market.Balance(Seller));
        Assert.Empty(f.Market.PersonalOrders(Seller));
        Assert.Empty(f.Market.Changes(Seller, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidOwnershipFundsSourcesAndModificationCannotChangeAssets(bool live)
    {
        using Fixture f = new(live);
        Assert.Throws<InvalidDataException>(() => f.Market.PlaceSell(Buyer, [new(Stack, f.Pilot.StationId, Ammo, 1000, 1)], 1));
        f.List();
        var order = f.Order();
        Assert.Throws<InvalidDataException>(() => f.Market.Cancel(Buyer, order.Id, order.RegionId));
        Assert.Throws<InvalidDataException>(() => f.Market.Cancel(Seller, order.Id, order.RegionId + 1));
        Assert.Throws<InvalidDataException>(() => f.Market.PlaceBuy(Buyer, new(f.Pilot.StationId, Ammo, long.MaxValue, 10, 1)));
        Assert.Throws<InvalidDataException>(() => f.Market.PlaceBuy(Buyer, new(f.Pilot.StationId, Ammo, Budget, 10, 1)));
        Assert.Equal(Budget, f.Market.Balance(Buyer));
        Assert.Equal(100, f.Order().Remaining);
        Assert.Single(f.Market.PersonalOrders(Seller));
        Assert.Empty(f.Market.PersonalOrders(Buyer));
        Assert.Empty(f.Market.Transactions(Buyer));
    }

    [Fact]
    public async Task BackgroundExpiryReturnsAssetsWithoutAConnectedOwnerAndRestartDoesNotRepeatIt()
    {
        using Fixture f = new();
        f.List(50);
        f.Market.PlaceBuy(Buyer, new(f.Pilot.StationId, Ammo, 500, 10, 1));
        var timerClock = new CheckpointClock(f.Clock);
        await using MarketMaintenance maintenance = new(f.Market, timerClock);
        maintenance.Start();
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(1));
        timerClock.FireDueTimers();
        await maintenance.Pending;
        Assert.Null(maintenance.LastFailure);
        Assert.Empty(f.Market.PersonalOrders(Seller));
        Assert.Empty(f.Market.PersonalOrders(Buyer));
        Assert.Equal(100, f.Quantity(Seller));
        Assert.Equal(Budget - 10_000, f.Market.Balance(Buyer));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(0, f.Market.Expire());
        Assert.Equal(100, f.Quantity(Seller));
        Assert.Equal(Budget - 10_000, f.Market.Balance(Buyer));
        Assert.Equal(2, f.Market.PersonalOrders(Seller, true).Count + f.Market.PersonalOrders(Buyer, true).Count);
    }

    [Fact]
    public void StartupExpiresOverdueOrdersAndMigrationPreservesDevelopmentSaves()
    {
        using Fixture f = new();
        f.List();
        f.Clock.AdvanceWallTime(TimeSpan.FromDays(2));
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(100, f.Quantity(Seller));
        Assert.Empty(f.Market.PersonalOrders(Seller));
        Assert.False(f.Market.Economy().TestingMarket);
        WorldInitialization.Initialize(new(f.Db.Path), f.Clock);
        Assert.Equal(100, f.Quantity(Seller));
        using var legacy = new TestWorld(emptyWallets: false);
        var market = legacy.Services.GetRequiredService<IPilotMarket>();
        long balance = market.Balance(Seller);
        using (WorldDbContext world = new(legacy.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TABLE WorldEconomy;
                DROP TABLE WorldPersonalOrder;
                DROP TABLE WorldMarketPayment;
                DROP TABLE WorldMarketChange;
                ALTER TABLE WorldMarketTrade DROP COLUMN WalletAmountCents;
                PRAGMA user_version=86;
                """
            );
        }
        WorldInitialization.Initialize(new(legacy.Path));
        var settings = legacy.Services.GetRequiredService<IPersonalMarket>().Economy();
        Assert.True(settings.TestingMarket);
        Assert.True(settings.TestingGrants);
        Assert.Equal(balance, market.Balance(Seller));
    }

    [Fact]
    public void FreshEconomicSettingsDisableStockAndGrantsAndSurviveReopen()
    {
        string directory = TestDirectory.Create("eden-economy-").FullName;
        try
        {
            var database = new WorldDatabase(System.IO.Path.Combine(directory, "world.sqlite"));
            TestCatalog.Create(database.CatalogPath);
            WorldInitialization.Initialize(database, economy: new(false, false));
            using var services = ServerServices.Build(database);
            var personal = services.GetRequiredService<IPersonalMarket>();
            var market = services.GetRequiredService<IPilotMarket>();
            Assert.Equal(new WorldEconomySettings(false, false), personal.Economy());
            Assert.Equal(0, market.Balance(Seller));
            Assert.Empty(market.Orders(Seller));
            WorldInitialization.Initialize(database);
            Assert.Equal(new WorldEconomySettings(false, false), personal.Economy());
            Assert.Throws<InvalidDataException>(() => WorldInitialization.Initialize(database, economy: new()));
            using WorldDbContext world = new(database.Path);
            Assert.Empty(world.Set<TestingWalletGrant>());
            Assert.Equal(WorldMigration.CurrentVersion, world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OrderSlotsEscrowModificationsAndRejectedBatchRemainAtomic(bool live)
    {
        using Fixture f = new(live);
        for (int index = 0; index < 5; index++)
        {
            f.List(1);
        }
        long balance = f.Market.Balance(Seller);
        Assert.Throws<InvalidDataException>(() => f.List(1));
        Assert.Equal(95, f.Quantity(Seller));
        Assert.Equal(balance, f.Market.Balance(Seller));
        f.Market.PlaceBuy(Buyer, new(f.Pilot.StationId, Ammo, 500, 5, 1));
        var order = f.Order(Buyer);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        long before = f.Market.Balance(Buyer);
        f.Market.Modify(Buyer, f.Modification(order, 400));
        Assert.Equal(before - 10_000 + 500, f.Market.Balance(Buyer));
        Assert.Equal(2000, f.Market.Escrow(Buyer).IskCents);
        var changed = f.Order(Buyer);
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        long available = f.Market.Balance(Buyer);
        Assert.Throws<InvalidDataException>(() => f.Market.Modify(Buyer, f.Modification(changed, Budget)));
        Assert.Equal(available, f.Market.Balance(Buyer));
        Assert.Equal(changed, f.Order(Buyer));
        Assert.Throws<InvalidDataException>(() => f.Market.Buy(Buyer, f.Pilot.StationId, [new(Ammo, 1000, 1), new(123456, 1, 1)]));
        Assert.Equal(0, f.Quantity(Buyer));
        Assert.Equal(5, f.Market.PersonalOrders(Seller).Count);
        Assert.Equal(available, f.Market.Balance(Buyer));
    }

    [Fact]
    public void IdleExpiryUsesNoSqlAndOrderChangesInvalidateItsDeadline()
    {
        using Fixture f = new();
        Assert.Equal(long.MaxValue, f.Market.NextExpiry);
        using (var measured = SessionPerformance.Begin("idle market expiry", _ => { }, force: true)!)
        {
            for (int index = 0; index < 10; index++)
            {
                Assert.Equal(0, f.Market.Expire());
            }
            Assert.Equal(0, measured.Commands);
            Assert.Equal(0, measured.Connections);
        }
        f.List();
        Assert.Equal(f.Clock.Now + TimeSpan.TicksPerDay, f.Market.NextExpiry);
        f.Market.Cancel(Seller, f.Order().Id, f.Pilot.RegionId);
        Assert.Equal(long.MaxValue, f.Market.NextExpiry);
    }

    [Fact]
    public void NativeFormsExposeEscrowOrdersAndCrossSessionNotifications()
    {
        using Fixture f = new();
        using var sellerScope = f.Db.Services.CreateScope();
        using var buyerScope = f.Db.Services.CreateScope();
        PilotCallContext Context(DockedPilot pilot) =>
            new(
                pilot.PilotId == Seller ? 1000001 : 1000002,
                pilot.Account,
                [pilot],
                p => new(f.Db.Store, p, f.Clock, new SharedSpace(), f.Db.Objects),
                1,
                SignedSettings.Empty
            );
        using var seller = Context(f.Pilot);
        var wing = f.Db.Store.FindPilot(1000002)!;
        using var buyer = Context(wing);
        seller.Select(f.Pilot);
        buyer.Select(wing);
        var handler = sellerScope.ServiceProvider.GetRequiredService<MarketHandler>();
        var monitor = sellerScope.ServiceProvider.GetRequiredService<PersonalMarketMonitor>();
        monitor.Enter(seller);
        var item = new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["itemID"] = Stack,
                ["stationID"] = (long)f.Pilot.StationId,
                ["typeID"] = (long)Ammo,
                ["quantity"] = 100L,
                ["price"] = 10d,
            }
        );
        Assert.IsType<PilotCallResult.Success>(
            handler.Call(seller, "marketProxy", "PlaceMultiSellOrder", [new List<object?> { item }, 0L, 1L, 0d])
        );
        Assert.Contains(seller.Notifications, n => n.Name == "OnOwnOrdersChanged");
        seller.Notifications.Clear();
        var buyHandler = buyerScope.ServiceProvider.GetRequiredService<MarketHandler>();
        Assert.IsType<PilotCallResult.Success>(
            buyHandler.Call(buyer, "marketProxy", "PlaceBuyOrder", [(long)wing.StationId, (long)Ammo, 10d, 40L, -1L, 1L, 0L, false, 0d])
        );
        monitor.Advance(seller);
        Assert.Contains(seller.Notifications, n => n.Name == "OnOwnOrdersChanged");
        Assert.Contains(seller.Notifications, n => n.Name == "OnAccountChange");
        Assert.Equal(60, f.Order().Remaining);
        var row = new MarketOrderRow(f.Order()).ToBlue();
        Assert.NotEmpty(BlueMarshal.Encode(row));
        var fields = Assert.IsType<object?[]>(Assert.IsType<BlueConstructor>(row).Arguments[1]);
        Assert.Equal(1, fields[13]);
        Assert.Equal(0d, fields[15]);
        var escrow = Assert.IsType<PilotCallResult.Success>(handler.Call(seller, "marketProxy", "GetCharEscrow", []));
        Assert.Equal(600d, Assert.IsType<EscrowInfo>(escrow.Reply).Items);
        var order = f.Order();
        f.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        Assert.Same(
            PilotCallResult.None,
            handler.Call(
                seller,
                "marketProxy",
                "ModifyCharOrder",
                [order.Id, 11d, false, (long)order.StationId, (long)order.SystemId, 10d, -1L, 60L, order.Issued]
            )
        );
        Assert.Equal(1100, f.Order().PriceCents);
        Assert.Same(PilotCallResult.None, handler.Call(seller, "marketProxy", "CancelCharOrder", [order.Id, (long)order.RegionId]));
        Assert.Equal(60, f.Quantity(Seller));
        Assert.Empty(f.Market.PersonalOrders(Seller));
        Assert.IsType<PilotCallResult.Success>(
            buyHandler.Call(buyer, "marketProxy", "PlaceBuyOrder", [(long)wing.StationId, (long)Ammo, 5d, 10L, -1L, 1L, 1L, 0L, 0d])
        );
        buyer.Notifications.Clear();
        long returned;
        using (WorldDbContext world = new(f.Db.Path))
        {
            returned = world.Set<OwnedItem>().Single(i => i.OwnerId == Seller && i.TypeId == Ammo).Id;
        }
        f.Market.Sell(Seller, [new(returned, f.Pilot.StationId, Ammo, 500, 10)]);
        buyerScope.ServiceProvider.GetRequiredService<PersonalMarketMonitor>().Advance(buyer);
        Assert.Contains(buyer.Notifications, n => n.Name == "OnItemChange");
        Assert.Contains(buyer.Notifications, n => n.Name == "OnOwnOrdersChanged");
        Assert.Equal(50, f.Quantity(Buyer));
    }
}
