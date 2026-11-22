using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Eden.Data.Catalog.Market;
using Eden.Data.World.Market;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Configuration;
using Eden.Gameplay.Market;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Market;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.Server.Administration;
using Eden.Server.Launcher;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class StandardMarketTests
{
    private const long Pilot = 90000001;
    private const int Hub = 60003760;
    private const int OtherStation = 60015157;
    private const int SystemId = 30000142;
    private const int Region = 10000002;
    private const int Tritanium = 34;
    private const long StartingBalance = 10_000_000;

    [Fact]
    public async Task LivePriceLevelsDepleteAndSurviveCheckpointWithoutRefilling()
    {
        TestClock clock = new();
        using TestWorld fixture = Create(clock);
        WorldDatabase database = new(fixture.Path);
        MarketOrder[] ordersBeforeCheckpoint;
        long balance;
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var market = host.GetRequiredService<IPilotMarket>();
            var book = market.Orders(Pilot, Tritanium);
            Assert.Equal(2, book.Count(order => !order.Bid && order.StationId == Hub));
            Assert.Equal(8, book.Where(order => !order.Bid).Sum(order => order.Remaining));
            var bid = Assert.Single(book, order => order.Bid);
            Assert.Equal((OtherStation, 0, 2, 90), (bid.StationId, bid.Range, bid.MinimumVolume, bid.DurationDays));
            var wire = Assert.IsType<object?[]>(MarketWire.Row(bid).Arguments[1]);
            Assert.Equal(0, wire[6]);
            Assert.Equal(2, wire[11]);
            Assert.Equal(90, wire[13]);
            Assert.NotEmpty(BlueMarshal.Encode(MarketWire.Row(bid)));
            Assert.Throws<InvalidDataException>(() => market.Buy(Pilot, Hub, [new(Tritanium, 99, 1)]));
            Assert.Equal(StartingBalance, market.Balance(Pilot));
            Assert.Empty(market.Transactions(Pilot));
            Assert.Equal(book, market.Orders(Pilot, Tritanium));
            var bought = market.Buy(Pilot, Hub, [new(Tritanium, 200, 6)]);
            Assert.Equal(new[] { 3, 3 }, bought.Trades.Select(trade => trade.Quantity));
            Assert.Equal(StartingBalance - 900, bought.BalanceCents);
            var ask = Assert.Single(market.Orders(Pilot, Tritanium), order => !order.Bid);
            Assert.Equal((200L, 2, 5), (ask.PriceCents, ask.Remaining, ask.Entered));
            long itemId = bought.Changes.Last().Item.Id;
            Assert.Throws<InvalidDataException>(() => market.Sell(Pilot, [new(itemId, Hub, Tritanium, 50, 1)]));
            Assert.Equal(bought.BalanceCents, market.Balance(Pilot));
            Assert.Equal(4, Assert.Single(market.Orders(Pilot, Tritanium), order => order.Bid).Remaining);
            var sold = market.Sell(Pilot, [new(itemId, Hub, Tritanium, 50, 2)]);
            Assert.Equal(2, Assert.Single(sold.Trades).Quantity);
            Assert.Equal(2, Assert.Single(market.Orders(Pilot, Tritanium), order => order.Bid).Remaining);
            ordersBeforeCheckpoint = market.Orders(Pilot, Tritanium).OrderBy(order => order.Id).ToArray();
            balance = sold.BalanceCents;
            var summary = market.BestAsks(Pilot, MarketScope.Region);
            using (var metrics = SessionPerformance.Begin("warm standard market", _ => { }, force: true)!)
            {
                Assert.Same(summary, market.BestAsks(Pilot, MarketScope.Region));
                _ = market.Orders(Pilot, Tritanium);
                Assert.Equal(0, metrics.Commands);
                Assert.Equal(0, metrics.Connections);
                Assert.Equal(0, metrics.FittingCalculations);
            }
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        var shop = reopened.GetRequiredService<IPilotMarket>();
        Assert.Equal(ordersBeforeCheckpoint, shop.Orders(Pilot, Tritanium).OrderBy(order => order.Id));
        Assert.Equal(balance, shop.Balance(Pilot));
        Assert.Equal(3, shop.Transactions(Pilot).Count);
        clock.AdvanceWallTime(TimeSpan.FromDays(90));
        Assert.Empty(shop.Orders(Pilot, Tritanium));
        Assert.Empty(shop.BestAsks(Pilot, MarketScope.Region));
    }

    [Fact]
    public async Task NewSnapshotUpdatesStockOnceAndRetiresRemovedOrders()
    {
        TestClock clock = new();
        using TestWorld fixture = Create(clock);
        WorldDatabase database = new(fixture.Path);
        long retained;
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var active = host.GetRequiredService<IPilotMarket>();
            retained = Assert.Single(active.Orders(Pilot, Tritanium), order => order.PriceCents == 100).Id;
            await host.GetRequiredService<LiveWorld>().FlushAsync();
            var snapshot = database.MarketSnapshot;
            var previous = snapshot.ReadManifest();
            string staging = Path.Combine(snapshot.Root, "staging");
            Assert.Throws<DirectoryNotFoundException>(() => OptionalDataImporter.PublishMarketSnapshot(snapshot, staging, default));
            Assert.Equal(previous.Generation, snapshot.ReadManifest().Generation);
            clock.AdvanceWallTime(TimeSpan.FromMinutes(30));
            WriteSnapshot(database, clock, [Order(clock, 1, 125, 7)], staging);
            OptionalDataImporter.PublishMarketSnapshot(snapshot, staging, default);
            Assert.NotEqual(previous.Generation, snapshot.ReadManifest().Generation);
            Assert.True(Directory.Exists(Path.Combine(snapshot.Root, previous.Generation)));
            Assert.Contains(active.Orders(Pilot, Tritanium), order => order.Id == retained && order.PriceCents == 100);
        }
        using var reopened = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        var market = reopened.GetRequiredService<IPilotMarket>();
        var only = Assert.Single(market.Orders(Pilot, Tritanium));
        Assert.Equal((retained, 125L, 7), (only.Id, only.PriceCents, only.Remaining));
        market.Buy(Pilot, Hub, [new(Tritanium, 125, 3)]);
        Assert.Equal(4, Assert.Single(market.Orders(Pilot, Tritanium)).Remaining);
        await reopened.GetRequiredService<LiveWorld>().FlushAsync();
    }

    [Fact]
    public void MissingSnapshotAndConflictingModesRejectConfiguration()
    {
        using TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        WorldConfigurationStore config = new(database, TimeProvider.System);
        var state = config.Read();
        var draft = state.Active.Draft with
        {
            Settings = state.Active.Draft.Settings with
            {
                Economy = new(ProgressiveMarkets: false, StandardMarket: true),
                DevelopmentSupply = new(TestingMarket: false),
            },
        };
        Assert.Throws<InvalidDataException>(() => config.Save(state.Generation, draft, "test"));
        Assert.Null(config.Read().Pending);
        var conflict = draft with { Settings = draft.Settings with { DevelopmentSupply = new(TestingMarket: true) } };
        Assert.Throws<InvalidDataException>(() => config.Validate(conflict));
        conflict = draft with { Settings = draft.Settings with { Economy = new(ProgressiveMarkets: true, StandardMarket: true) } };
        Assert.Throws<InvalidDataException>(() => config.Validate(conflict));
    }

    [Theory]
    [InlineData(MarketPreset.NpcOnly, NpcMarketKind.Institutional)]
    [InlineData(MarketPreset.Standard, NpcMarketKind.Standard)]
    [InlineData(MarketPreset.Testing, NpcMarketKind.Development)]
    public async Task FreshWorldCanSwitchToRegularMarketAndRetainPlayerOrders(MarketPreset preset, NpcMarketKind expectedKind)
    {
        const int school = 18;
        const int system = 30001392;
        const int station = 60015159;
        const int shuttle = 672;
        var directory = TestDirectory.Create("eden-fresh-market-");
        try
        {
            WorldDatabase database = new(Path.Combine(directory.FullName, "world.sqlite"));
            TestCatalog.Create(database.OriginalCatalogPath);
            TestCatalog.AddSchool(database.OriginalCatalogPath, school, 1, 1, system, station, 601);
            using (CatalogDbContext catalog = new(database.OriginalCatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT OR IGNORE INTO ItemGroup VALUES(31,'Shuttle',6);
                    INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES(672,'Caldari Shuttle',31);
                    INSERT OR REPLACE INTO SourceRecord VALUES
                        ('client/stationoperations','14','{{"services":[7]}}'),
                        ('client/types','672','{{"published":1,"marketGroupID":1,"volume":5000}}'),
                        ('client/npccorporations','1000035','{{"corporationTrades":{{"672":1}}}}');
                    """
                );
            }
            TestClock clock = new();
            WorldInitialization.Initialize(database, clock, new(FreshUniverse: true));
            var configuration = new WorldConfigurationStore(database, clock);
            long pilot;
            long balance;
            MarketOrder order;
            using (var host = ServerServices.Build(database, clock, liveWorld: true))
            {
                long account = host.GetRequiredService<LauncherStore>()
                    .Register(new("Market Account", "Isolated Test Password 1"))
                    .AccountId;
                pilot = host.GetRequiredService<ICharacterCreation>()
                    .Create(account, new("Market Pilot", 1, 1, 0, CharacterRules.UnspecifiedAncestry, [], [], school));
                var market = host.GetRequiredService<IPilotMarket>();
                Assert.Equal(0, market.Balance(pilot));
                Assert.Empty(market.Orders(pilot, shuttle));
                var live = host.GetRequiredService<LiveWorld>();
                using (var economy = live.Economy.Begin())
                {
                    economy.Add(new PilotWallet { PilotId = pilot, Cents = StartingBalance });
                    economy.Commit();
                }
                var personal = host.GetRequiredService<IPersonalMarket>();
                personal.PlaceBuy(pilot, new(station, shuttle, 100, 1, 90));
                order = Assert.Single(personal.PersonalOrders(pilot));
                balance = market.Balance(pilot);

                var state = configuration.Read();
                var form = SettingsForm.From(state.Active.Draft.Settings);
                form.MarketPreset = preset;
                var draft = state.Active.Draft with { Settings = form.Build() };
                if (preset == MarketPreset.Standard)
                {
                    var missing = Assert.Throws<InvalidDataException>(() => configuration.Save(state.Generation, draft, "test"));
                    Assert.Contains("Import a compatible live market order snapshot", missing.Message);
                    WriteSnapshot(
                        database,
                        clock,
                        [Order(clock, 1, 70_000, 3) with { StationId = station, SystemId = system, TypeId = shuttle }]
                    );
                }
                var pending = configuration.Save(state.Generation, draft, "test");
                Assert.NotNull(pending.Pending);
                Assert.DoesNotContain(market.Orders(pilot, shuttle), row => !row.Bid);
                await live.FlushAsync();
            }

            WorldInitialization.Initialize(database, clock);
            using var reopened = ServerServices.Build(database, clock, liveWorld: true);
            Assert.True(database.Settings.FreshUniverse);
            Assert.False(database.Settings.DevelopmentSupply.TestingGrants);
            Assert.Equal(preset, SettingsForm.From(database.Settings).MarketPreset);
            var shop = reopened.GetRequiredService<IPilotMarket>();
            var ask = Assert.Single(shop.Orders(pilot, shuttle), row => !row.Bid);
            Assert.Equal(station, ask.StationId);
            var restored = reopened.GetRequiredService<LiveWorld>();
            Assert.Equal(expectedKind, restored.Economy.Inspect(world => world.Set<NpcMarketOrder>().Find(ask.Id)!.Kind));
            Assert.Equal(balance, shop.Balance(pilot));
            Assert.Equal(order, Assert.Single(reopened.GetRequiredService<IPersonalMarket>().PersonalOrders(pilot)));
            Assert.Single(shop.Buy(pilot, station, [new(shuttle, ask.PriceCents, 1)]).Trades);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void CorruptRegionDoesNotPublishPartialOrdersOrChangeTheWallet()
    {
        TestClock clock = new();
        using TestWorld fixture = Create(clock);
        WorldDatabase database = new(fixture.Path);
        var manifest = database.MarketSnapshot.ReadManifest();
        string path = Path.Combine(Path.GetDirectoryName(fixture.Path)!, "market-orders", manifest.Generation, $"{Region}.json.gz");
        File.AppendAllText(path, "corrupt");
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        Assert.Throws<InvalidDataException>(() => market.Orders(Pilot, Tritanium));
        Assert.Equal(StartingBalance, market.Balance(Pilot));
        using var action = host.GetRequiredService<LiveWorld>().Economy.Begin();
        Assert.Empty(action.Set<NpcMarketOrder>().Where(order => order.Kind == NpcMarketKind.Standard));
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(32767, true)]
    public void RemoteLiveBidsHonorTheirJumpRange(int range, bool matches)
    {
        TestClock clock = new();
        using TestWorld fixture = Create(clock, remoteSystem: 30000144, remoteRange: range);
        using var host = ServerServices.Build(new(fixture.Path), clock, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        var bought = market.Buy(Pilot, Hub, [new(Tritanium, 100, 2)]);
        var sale = new MarketSale(Assert.Single(bought.Changes).Item.Id, Hub, Tritanium, 50, 2);
        if (matches)
        {
            Assert.Equal(2, Assert.Single(market.Sell(Pilot, [sale]).Trades).Quantity);
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => market.Sell(Pilot, [sale]));
            Assert.Equal(bought.BalanceCents, market.Balance(Pilot));
            Assert.Equal(4, Assert.Single(market.Orders(Pilot, Tritanium), order => order.Bid).Remaining);
        }
    }

    [Fact]
    public void PopulatedLiveBookRetainsIndividualOrdersAndWarmReadsAvoidSql()
    {
        const int depth = 2500;
        TestClock clock = new();
        using TestWorld fixture = Create(clock);
        WorldDatabase database = new(fixture.Path);
        WriteSnapshot(database, clock, Enumerable.Range(1, depth).Select(id => Order(clock, id, id + 100, 3)).ToArray());
        using var host = ServerServices.Build(database, clock, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        Assert.Equal(depth, market.Orders(Pilot, Tritanium).Count);
        var best = Assert.Single(market.BestAsks(Pilot, MarketScope.Region));
        Assert.Equal((101L, 3), (best.PriceCents, best.Remaining));
        market.Buy(Pilot, Hub, [new(Tritanium, 101, 1)]);
        _ = market.BestAsks(Pilot, MarketScope.Region);
        using var metrics = SessionPerformance.Begin("populated standard book", _ => { }, force: true)!;
        Assert.Equal(depth, market.Orders(Pilot, Tritanium).Count);
        var purchase = market.Buy(Pilot, Hub, [new(Tritanium, 102, 3)]);
        Assert.Equal(3, purchase.Trades.Sum(trade => trade.Quantity));
        Assert.Equal(StartingBalance - 405, purchase.BalanceCents);
        Assert.Equal(102, Assert.Single(market.BestAsks(Pilot, MarketScope.Region)).PriceCents);
        Assert.Equal(0, metrics.Commands);
        Assert.Equal(0, metrics.Connections);
        Assert.Equal(0, metrics.FittingCalculations);
    }

    [Theory]
    [InlineData(1, 10, 2, false)]
    [InlineData(2, 10, 2, true)]
    [InlineData(1, 1, 2, true)]
    public void MinimumVolumeAllowsTheFinalPartialStack(int request, int remaining, int minimum, bool expected) =>
        Assert.Equal(expected, StandardMarketRules.CanFill(request, remaining, minimum));

    [Theory]
    [InlineData(-1, false, 0, false)]
    [InlineData(-1, true, 0, true)]
    [InlineData(0, false, 0, true)]
    [InlineData(0, false, 1, false)]
    [InlineData(5, false, 5, true)]
    [InlineData(5, false, 6, false)]
    [InlineData(32767, false, null, true)]
    public void BuyRangeUsesStationSystemJumpAndRegionBoundaries(int range, bool sameStation, int? jumps, bool expected) =>
        Assert.Equal(expected, StandardMarketRules.Reaches(range, sameStation, jumps));

    private static TestWorld Create(TestClock clock, int remoteSystem = SystemId, int remoteRange = 0)
    {
        TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[7,14]}}');
                INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (4,'Material');
                INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (18,'Mineral',4);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES (34,'Tritanium',18);
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/types','34','{{"published":1,"marketGroupID":1,"volume":0.01}}');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60015157,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
            catalog.Database.ExecuteSql($"UPDATE Station SET SolarSystemId={remoteSystem} WHERE Id={OtherStation}");
        }
        using (WorldDbContext world = new(fixture.Path))
        {
            world.Add(new PilotWallet { PilotId = Pilot, Cents = StartingBalance });
            world.SaveChanges();
        }
        WriteSnapshot(
            database,
            clock,
            [
                Order(clock, 1, 100, 3),
                Order(clock, 2, 200, 5),
                Order(clock, 3, 50, 4) with
                {
                    Bid = true,
                    StationId = OtherStation,
                    SystemId = remoteSystem,
                    MinimumVolume = 2,
                    Range = remoteRange,
                },
            ]
        );
        WorldConfigurationStore configuration = new(database, clock);
        var state = configuration.Read();
        configuration.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with
                {
                    Economy = new(ProgressiveMarkets: false, StandardMarket: true),
                    DevelopmentSupply = new(TestingMarket: false, TestingGrants: false),
                },
            },
            "test"
        );
        WorldInitialization.Initialize(database, clock);
        return fixture;
    }

    private static LiveMarketOrder Order(TestClock clock, long id, long price, int quantity) =>
        new(id, Hub, SystemId, Region, Tritanium, false, price, quantity, quantity, clock.Now, 90, -1, 1);

    internal static void InstallSetupSnapshot(WorldDatabase database)
    {
        TestClock clock = new();
        WriteSnapshot(database, clock, [Order(clock, 1, 200, 5)]);
    }

    internal static void WriteSnapshot(WorldDatabase database, TestClock clock, LiveMarketOrder[] orders, string? output = null)
    {
        string generation = Guid.NewGuid().ToString("N");
        string root = output ?? database.MarketSnapshot.Root;
        Directory.CreateDirectory(Path.Combine(root, generation));
        int region = orders.Select(order => order.RegionId).Distinct().Single();
        string regionPath = Path.Combine(root, generation, $"{region}.json.gz");
        using (var file = File.Create(regionPath))
        using (GZipStream compressed = new(file, CompressionLevel.Fastest))
        {
            JsonSerializer.Serialize(compressed, orders, JsonSerializerOptions.Web);
        }
        using var stream = File.OpenRead(regionPath);
        LiveMarketSnapshotManifest manifest = new(
            1,
            generation,
            "test",
            clock.GetUtcNow(),
            database.Catalog.Identity.SourceFingerprint,
            orders.Length,
            new Dictionary<string, int>(),
            [new(region, orders.Length, Convert.ToHexString(SHA256.HashData(stream)))]
        );
        File.WriteAllText(Path.Combine(root, "manifest.json"), JsonSerializer.Serialize(manifest, JsonSerializerOptions.Web));
    }
}
