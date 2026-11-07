using Eden.Data.Catalog;
using Eden.Data.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Market;
using Eden.Protocol.Characters;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class TestingMarketTests
{
    private const long PilotId = 90000001;
    private const int StationId = 60003760;

    [Fact]
    public void PublishedMarketTypesAreSeededExactlyOnceAndUnsupportedTypesCanBePurchased()
    {
        using TestWorld db = new(emptyWallets: false);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}');
                INSERT INTO ItemType(Id,Name,GroupId) VALUES
                    (10001,'Unsupported module',54),(10002,'Hidden module',54),(10003,'No market group',54),
                    (10004,'Missing publication',54),(44992,'PLEX',54);
                INSERT INTO SourceRecord VALUES
                    ('client/types','10001','{{"published":1,"marketGroupID":2,"volume":5}}'),
                    ('client/types','10002','{{"published":0,"marketGroupID":2}}'),
                    ('client/types','10003','{{"published":1}}'),
                    ('client/types','10004','{{"marketGroupID":2}}'),
                    ('client/types','44992','{{"published":1,"marketGroupID":3,"volume":0.01}}'),
                    ('client/types','10005','{{"published":1,"marketGroupID":2,"volume":5}}');
                """
            );
        }
        var market = db.Services.GetRequiredService<IPilotMarket>();
        var asks = market.BestAsks(PilotId, MarketScope.Station);
        Assert.Equal(new[] { 3651, 10001, 44992 }, asks.Select(o => o.TypeId).Order().ToArray());
        Assert.All(asks, o => Assert.Equal((100L, 1_000_000), (o.PriceCents, o.Remaining)));
        Assert.Equal(asks, market.BestAsks(PilotId, MarketScope.Region));
        var purchase = market.Buy(PilotId, StationId, [new(10001, 100, 2), new(44992, 100, 1)]);
        Assert.Equal(TestingMarketRules.GrantCents - 300, purchase.BalanceCents);
        Assert.Equal(new[] { 10001, 44992 }, purchase.Changes.Select(c => c.Item.TypeId).Order().ToArray());
        Assert.Single(market.Orders(PilotId, 10001));
        Assert.Empty(market.Orders(PilotId, 10002));
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        using var scope = db.Services.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<MarketHandler>();
        var quote = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "marketProxy", "GetPlexOrders", []));
        var cached = Assert.IsType<CachedMarketOrders>(quote.Reply);
        var book = Assert.IsType<MarketOrderBook>(cached.Result);
        Assert.Equal(44992, Assert.Single(book.Sell).TypeId);
        Assert.Empty(book.Buy);
        var best = Assert.IsType<PilotCallResult.Success>(handler.Call(context, "marketProxy", "GetPlexBest", []));
        Assert.Single(Assert.IsType<BlueMap<long, MarketOrderRow>>(best.Reply).Entries);
        _ = WorldInitialization.Open(db.Path);
        Assert.All(market.BestAsks(PilotId, MarketScope.Station), o => Assert.Equal(1_000_000, o.Remaining));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(3, saved.Set<NpcMarketOrder>().Count());
        Assert.Equal(2, saved.Set<MarketExecution>().Count());
    }

    [Fact]
    public void VersionTwentySevenAddsOneGrantToExistingMoneyWithoutSeedingOrders()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                TestWorld.DropTestingMarketSchema + "INSERT INTO WorldWallet VALUES (90000001,12345); PRAGMA user_version=27"
            );
        }
        TestClock clock = new(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.Zero));
        WorldInitialization.Initialize(new(db.Path), clock);
        WorldInitialization.Initialize(new(db.Path), clock);
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(100_012_345, wallet.Balance(PilotId));
        Assert.Equal(100_000_000, wallet.Balance(90000002));
        var grant = Assert.Single(wallet.Journal(PilotId).Grants);
        Assert.Equal((100_000_000L, 100_012_345L, clock.Now), (grant.AmountCents, grant.BalanceCents, grant.Time));
        using WorldDbContext saved = new(db.Path);
        Assert.Empty(saved.Set<NpcMarketOrder>());
        Assert.Equal(2, saved.Set<TestingWalletGrant>().Count());
    }

    [Fact]
    public void GrantJournalUsesNativeCashTransferAndNewPilotsReceiveFundsWithinCreation()
    {
        using TestWorld db = new(emptyWallets: false);
        TestCatalog.AddSchool(new WorldDatabase(db.Path).CatalogPath, 18, 1, 1, 30001392, 60015159, 601);
        var creation = db.Services.GetRequiredService<ICharacterCreation>();
        long id = creation.Create(1000001, new("Funded Pilot", 1, 1, 1, 64, [1], [2], 18));
        _ = WorldInitialization.Open(db.Path);
        var wallet = db.Services.GetRequiredService<IPilotWalletReader>();
        Assert.Equal(TestingMarketRules.GrantCents, wallet.Balance(id));
        Assert.Single(wallet.Journal(id).Grants);
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            1,
            SignedSettings.Empty
        );
        var selection = db.Services.GetRequiredService<CharacterSelectionHandler>();
        var selectionResult = Assert.IsType<PilotCallResult.Success>(
            selection.Call(context, "charUnboundMgr", "GetCharacterSelectionData", Array.Empty<object?>(), null)
        );
        var card = Assert.Single(Assert.IsType<CharacterSelectionReply>(selectionResult.Reply).Pilots);
        var cardFields = Assert.IsType<Dictionary<string, object?>>(Assert.IsType<BlueInstance>(card.ToBlue()).State);
        Assert.Equal(1_000_000d, cardFields["balance"]);
        context.Select(pilot);
        var handler = db.Services.GetRequiredService<WalletHandler>();
        var result = Assert.IsType<PilotCallResult.Success>(
            handler.Call(context, "account", "GetTransactions", [1000L, null, null, false])
        );
        var entries = Assert.IsType<BlueList<WalletJournalEntry>>(result.Reply);
        var wire = Assert.IsType<List<object?>>(entries.ToBlue());
        var row = Assert.IsType<BlueInstance>(Assert.IsType<WalletJournalEntry>(Assert.Single(wire)).ToBlue());
        var fields = Assert.IsType<Dictionary<string, object?>>(row.State);
        Assert.Equal(3, fields["entryTypeID"]);
        Assert.Equal(1_000_000d, fields["amount"]);
    }

    [Fact]
    public void FailedGrantRollsBackTheMigrationAndExistingBalance()
    {
        using TestWorld db = new();
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(TestWorld.DropTestingMarketSchema + "PRAGMA user_version=27");
            world.Add(new PilotWallet { PilotId = PilotId, Cents = long.MaxValue });
            world.SaveChanges();
        }
        Assert.Throws<OverflowException>(() => WorldInitialization.Initialize(new(db.Path)));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(27, saved.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single());
        Assert.Equal(long.MaxValue, saved.Set<PilotWallet>().Single().Cents);
        Assert.DoesNotContain(
            saved.Database.SqlQueryRaw<string>("SELECT name AS Value FROM sqlite_master WHERE type='table'"),
            name => name == "WorldTestingGrant"
        );
    }

    [Fact]
    public async Task CompetingPurchasesUseReplenishingSupplyAndKeepIndependentWallets()
    {
        using TestWorld db = new(emptyWallets: false);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[7]}}')");
        }
        var market = db.Services.GetRequiredService<IPilotMarket>();
        var results = await Task.WhenAll(
            Task.Run(() => market.Buy(PilotId, StationId, [new(3651, 100, 600_000)])),
            Task.Run(() => market.Buy(90000002, StationId, [new(3651, 100, 600_000)]))
        );
        Assert.All(results, r => Assert.Equal(40_000_000, r.BalanceCents));
        Assert.Equal(1_200_000, results.Sum(r => Assert.Single(r.Trades).Quantity));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(1_000_000, saved.Set<NpcMarketOrder>().Single().Remaining);
        Assert.Equal(2, saved.Set<MarketExecution>().Count());
    }

    [Fact]
    public void BooksRespectStationSystemRegionAndMarketService()
    {
        using TestWorld db = new(emptyWallets: false);
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7]}}');
                INSERT INTO Station(Id,SolarSystemId,TypeId,OwnerId,OperationId,OrbitId,X,Y,Z)
                    VALUES (60003761,30000142,52678,1000035,14,40009087,1,2,3),
                    (60003762,30000144,52678,1000035,14,40009087,4,5,6),
                    (60003763,30000144,52678,1000035,99,40009087,4,5,6);
                INSERT INTO SourceRecord VALUES ('client/stationoperations','99','{{"services":[]}}');
                """
            );
        }
        var market = db.Services.GetRequiredService<IPilotMarket>();
        var first = Assert.Single(market.Orders(PilotId, 3651));
        foreach (int station in new[] { 60003761, 60003762 })
        {
            Move(station);
            _ = market.Orders(PilotId, 3651);
        }
        Assert.Equal(3, market.Orders(PilotId, 3651).Count);
        Assert.Equal(60003762, Assert.Single(market.BestAsks(PilotId, MarketScope.Station)).StationId);
        Assert.Equal(60003762, Assert.Single(market.BestAsks(PilotId, MarketScope.System)).StationId);
        Assert.Equal(first.Id, Assert.Single(market.BestAsks(PilotId, MarketScope.Region)).Id);
        using (WorldDbContext world = new(db.Path))
        {
            world.Add(
                new NpcMarketOrder
                {
                    StationId = 60003764,
                    SystemId = 30000142,
                    RegionId = 10000003,
                    TypeId = 3651,
                    PriceCents = 1,
                    Remaining = 1,
                    Entered = 1,
                    Issued = DateTime.UtcNow.ToFileTimeUtc(),
                }
            );
            world.SaveChanges();
        }
        Assert.Equal(3, market.Orders(PilotId, 3651).Count);
        Move(60003763);
        Assert.Empty(market.BestAsks(PilotId, MarketScope.Station));
        Assert.Throws<InvalidDataException>(() => market.Buy(PilotId, 60003763, [new(3651, 100, 1)]));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(3, saved.Set<MarketStation>().Count());

        void Move(int station)
        {
            using WorldDbContext world = new(db.Path);
            world.Ships.Find(1000000000001L)!.StationId = station;
            world.SaveChanges();
        }
    }
}
