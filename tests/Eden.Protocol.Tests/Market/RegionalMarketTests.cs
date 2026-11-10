using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Protocol.Tests.Capsuleers;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Configuration;
using Eden.World.Docking;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class RegionalMarketTests
{
    private const int Source = 30000141;
    private const int HubStation = 60003760;
    private const int Ammunition = 215;
    private const int Merlin = 603; // Audited starter production cohort.

    [Fact]
    public void HubsUseOnlyHighSecurityAndGeneratedStockNeverAppearsInLowSecurity()
    {
        using CapsuleerHaulerTests.Fixture f = UnlockedFixture();
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/systems','30000141','{{\"securityStatus\":0.1}}')"
            );
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var orders = host.GetRequiredService<IPilotMarket>().Orders(pilot.PilotId, Ammunition);
        Assert.DoesNotContain(orders, o => o.StationId == pilot.StationId);
        Assert.Contains(orders, o => o.StationId == HubStation);
        using CatalogDbContext source = new(f.Database.CatalogPath);
        Assert.Single(RegionalMarketCatalog.Read(source).Stations, s => s.Role == RegionalMarketRole.Hub);
    }

    [Theory]
    [InlineData(InventoryCategory.Blueprint, 167, false)]
    [InlineData(InventoryCategory.Ship, 30, false)]
    [InlineData(InventoryCategory.Ship, 659, false)]
    [InlineData(InventoryCategory.Ship, 547, true)]
    [InlineData(InventoryCategory.Ship, 485, true)]
    [InlineData(InventoryCategory.Module, 53, true)]
    public void BlueprintAndSupercapitalExclusionsDoNotBlockOrdinaryCapitals(InventoryCategory category, int group, bool eligible)
    {
        Assert.Equal(eligible, RegionalMarketRules.CanUnlock(category, group, true, true));
        Assert.False(RegionalMarketRules.CanUnlock(category, group, false, true));
        Assert.False(RegionalMarketRules.CanUnlock(category, group, true, false));
    }

    [Fact]
    public async Task FiniteTradesRecoverOnSharedPlaytimeAndRespectNonCrossingPlayerListings()
    {
        using CapsuleerHaulerTests.Fixture f = UnlockedFixture();
        int remaining;
        long orderId;
        long restingId;
        long balance;
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
            using var selected = host.GetRequiredService<IHumanPresence>().Select(pilot.PilotId, Source);
            var market = host.GetRequiredService<IPilotMarket>();
            var quote = market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && !o.Bid);
            using (WorldDbContext funded = new(f.Database.Path))
            {
                long funds = checked(quote.PriceCents * 20000);
                var wallet =
                    funded.Set<PilotWallet>().Find(pilot.PilotId) ?? funded.Add(new PilotWallet { PilotId = pilot.PilotId }).Entity;
                wallet.Cents = funds;
                funded.SaveChanges();
            }
            Assert.Equal(1000, quote.Remaining);
            Assert.DoesNotContain(market.Orders(pilot.PilotId), o => o.TypeId == HaulingRules.BadgerType && !o.Bid);
            market.Buy(pilot.PilotId, pilot.StationId, [new(Ammunition, quote.PriceCents, 100)]);
            var depleted = market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && !o.Bid);
            Assert.Equal(quote.Remaining - 100, depleted.Remaining);
            Assert.True(depleted.PriceCents > quote.PriceCents);
            Assert.Equal(depleted, market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && !o.Bid));
            using (WorldDbContext world = new(f.Database.Path))
            {
                var item = world.Set<OwnedItem>().Single(i => i.OwnerId == pilot.PilotId && i.TypeId == Ammunition);
                host.GetRequiredService<IPersonalMarket>()
                    .PlaceSell(pilot.PilotId, [new(item.Id, pilot.StationId, Ammunition, quote.PriceCents * 2, 100)], 30);
                restingId = world.Set<SavedPersonalOrder>().Single(o => o.PilotId == pilot.PilotId && !o.Bid).Id;
            }
            using var friend = host.GetRequiredService<IHumanPresence>().Select(pilot.PilotId + 1, Source);
            long start = host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks;
            f.Clock.Seconds += 300;
            Assert.Equal(TimeSpan.FromMinutes(5).Ticks, host.GetRequiredService<IHumanPresence>().Read().ElapsedTicks - start);
            Assert.Equal(
                depleted.Remaining,
                market
                    .Orders(pilot.PilotId, Ammunition)
                    .Single(o => o.StationId == pilot.StationId && !o.Bid && o.OwnerId != pilot.PilotId)
                    .Remaining
            );
            f.Clock.Seconds += 300;
            var recovered = market
                .Orders(pilot.PilotId, Ammunition)
                .Single(o => o.StationId == pilot.StationId && !o.Bid && o.OwnerId != pilot.PilotId);
            Assert.True(recovered.Remaining > depleted.Remaining);
            Assert.True(recovered.PriceCents < depleted.PriceCents);
            remaining = recovered.Remaining;
            orderId = recovered.Id;
            balance = market.Balance(pilot.PilotId);
            Assert.Single(market.Transactions(pilot.PilotId));
            using (WorldDbContext world = new(f.Database.Path))
            {
                Assert.Equal(PersonalOrderStatus.Open, world.Set<SavedPersonalOrder>().Single(o => o.Id == restingId).Status);
            }
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        f.Clock.Seconds += 86400;
        WorldInitialization.Initialize(f.Database, f.Clock);
        using (var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true))
        {
            var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
            var market = host.GetRequiredService<IPilotMarket>();
            Assert.Equal(remaining, market.Orders(pilot.PilotId, Ammunition).Single(o => o.Id == orderId).Remaining);
            Assert.Equal(balance, market.Balance(pilot.PilotId));
            using WorldDbContext world = new(f.Database.Path);
            Assert.Equal(100, world.Set<SavedPersonalOrder>().Single(o => o.Id == restingId).Remaining);
        }
    }

    [Fact]
    public void PlaytimeNeverUnlocksUnclaimedProducts()
    {
        using CapsuleerHaulerTests.Fixture f = new(legacyFounders: false);
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        using var selected = host.GetRequiredService<IHumanPresence>().Select(pilot.PilotId, Source);
        var market = host.GetRequiredService<IPilotMarket>();
        Assert.DoesNotContain(market.Orders(pilot.PilotId), o => o.TypeId == HaulingRules.BadgerType && !o.Bid);
        f.Clock.Seconds += 21 * 86400;
        Assert.DoesNotContain(market.Orders(pilot.PilotId), o => o.TypeId == HaulingRules.BadgerType && !o.Bid);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Add(
                new SavedMarketUnlock
                {
                    TypeId = HaulingRules.BadgerType,
                    AccountId = 1000001,
                    PilotId = pilot.PilotId,
                    JobId = 1,
                    ClaimedAt = f.Clock.Now,
                }
            );
            world.SaveChanges();
        }
        var orders = market.Orders(pilot.PilotId);
        Assert.Contains(orders, o => o.TypeId == HaulingRules.BadgerType && !o.Bid && o.StationId == HubStation && o.Remaining > 0);
        Assert.DoesNotContain(orders, o => o.TypeId == Merlin && !o.Bid);
        Assert.Empty(market.Transactions(pilot.PilotId));
        Assert.All(
            market.History(pilot.PilotId, [HaulingRules.BadgerType]),
            day => Assert.Contains(day, MarketHistoryCatalog.Days(HaulingRules.BadgerType))
        );
    }

    [Fact]
    public void OneClaimStocksAnotherRegionAndARegionWithoutHighSecurityGetsNoGeneratedSupply()
    {
        using CapsuleerHaulerTests.Fixture f = UnlockedFixture();
        const int secondHub = 60008494;
        const int unsafeStation = 60009999;
        using (CatalogDbContext catalog = new(f.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Region(Id,Name) VALUES(10000043,'Second region'),(10000999,'Unsafe region');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES(20000998,10000043,'Second'),(20000999,10000999,'Unsafe');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES(30002187,20000998,'Second hub'),(30009999,20000999,'Unsafe');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60008494,TypeId,OwnerId,30002187,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60009999,TypeId,OwnerId,30009999,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                INSERT INTO SourceRecord VALUES('client/systems','30002187','{{"securityStatus":1.0}}'),
                    ('client/systems','30009999','{{"securityStatus":0.1}}');
                """
            );
        }
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var market = host.GetRequiredService<IPilotMarket>();
        Assert.Contains(market.Orders(pilot.PilotId, Ammunition), o => o.StationId == HubStation);
        foreach (int station in new[] { secondHub, unsafeStation })
        {
            using (WorldDbContext world = new(f.Database.Path))
            {
                world.Ships.Single(s => s.Id == pilot.ShipId).StationId = station;
                world.SaveChanges();
            }
            var orders = market.Orders(pilot.PilotId, Ammunition);
            if (station == secondHub)
            {
                Assert.Contains(orders, o => o.StationId == secondHub && o.RegionId == 10000043 && !o.Bid);
            }
            else
            {
                Assert.Empty(orders);
            }
        }
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Single(saved.Set<SavedMarketUnlock>());
    }

    [Fact]
    public void RecoveryIsFiniteAndRepeatedReadsPreserveFractionalIntervals()
    {
        RegionalStock empty = new(0, 60, 0);
        long halfway = RegionalMarketRules.RecoveryIntervalTicks / 2;
        Assert.Equal(empty, RegionalMarketRules.Recover(empty, 60, halfway));
        var first = RegionalMarketRules.Recover(empty, 60, halfway * 2);
        Assert.Equal(10, first.Remaining);
        Assert.Equal(first, RegionalMarketRules.Recover(first, 60, halfway * 3));
        Assert.Equal(60, RegionalMarketRules.Recover(first, 60, TimeSpan.FromDays(100).Ticks).Remaining);
        Assert.True(RegionalMarketRules.Quote(100, true, 0, 60) < RegionalMarketRules.Quote(100, false, 0, 60));
    }

    [Fact]
    public async Task CompetingBuyersCanPurchaseTheLastUnitOnlyOnce()
    {
        using CapsuleerHaulerTests.Fixture f = UnlockedFixture();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        _ = market.Orders(pilot.PilotId, Ammunition);
        long[] buyers;
        using (WorldDbContext world = new(f.Database.Path))
        {
            buyers = world.Pilots.Where(p => p.AccountId > 0).OrderBy(p => p.Id).Select(p => p.Id).Take(2).ToArray();
            world
                .Set<NpcMarketOrder>()
                .Where(o => o.StationId == pilot.StationId && o.TypeId == Ammunition && !o.Bid)
                .ExecuteUpdate(s => s.SetProperty(o => o.Remaining, 1));
        }
        var quote = market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && !o.Bid);
        foreach (long buyer in buyers)
        {
            Fund(f.Database, buyer, quote.PriceCents * 10);
        }
        var results = await Task.WhenAll(
            buyers.Select(buyer =>
                Task.Run(() =>
                {
                    try
                    {
                        market.Buy(buyer, pilot.StationId, [new(Ammunition, quote.PriceCents, 1)]);
                        return true;
                    }
                    catch (InvalidDataException)
                    {
                        return false;
                    }
                })
            )
        );
        Assert.Single(results, success => success);
        using WorldDbContext saved = new(f.Database.Path);
        Assert.Equal(0, saved.Set<NpcMarketOrder>().Single(o => o.Id == quote.Id).Remaining);
        Assert.Equal(1, saved.Set<OwnedItem>().Where(i => buyers.Contains(i.OwnerId) && i.TypeId == Ammunition).Sum(i => i.Quantity));
        Assert.Single(saved.Set<MarketExecution>());
    }

    [Fact]
    public void FailedPaymentRollsBackStockGoodsAndHistoryAndRetrySettlesOnce()
    {
        using CapsuleerHaulerTests.Fixture f = UnlockedFixture();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var quote = market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && !o.Bid);
        Fund(f.Database, pilot.PilotId, quote.PriceCents * 100);
        long balance = market.Balance(pilot.PilotId);
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER RejectRegionalPayment BEFORE UPDATE ON WorldWallet
                WHEN NEW.Cents < OLD.Cents BEGIN SELECT RAISE(ABORT,'payment failure'); END
                """
            );
        }
        Assert.Throws<DbUpdateException>(() => market.Buy(pilot.PilotId, pilot.StationId, [new(Ammunition, quote.PriceCents, 10)]));
        Assert.Equal(balance, market.Balance(pilot.PilotId));
        Assert.Empty(market.Transactions(pilot.PilotId));
        using (WorldDbContext world = new(f.Database.Path))
        {
            Assert.Empty(world.Set<OwnedItem>().Where(i => i.OwnerId == pilot.PilotId && i.TypeId == Ammunition));
            Assert.Equal(quote.Remaining, world.Set<NpcMarketOrder>().Single(o => o.Id == quote.Id).Remaining);
            world.Database.ExecuteSqlRaw("DROP TRIGGER RejectRegionalPayment");
        }
        market.Buy(pilot.PilotId, pilot.StationId, [new(Ammunition, quote.PriceCents, 10)]);
        Assert.Single(market.Transactions(pilot.PilotId));
    }

    [Fact]
    public void LocalResaleLosesMoneyAndTheHubCarriesMoreStockAndABetterBid()
    {
        using CapsuleerHaulerTests.Fixture f = UnlockedFixture();
        using var host = ServerServices.Build(f.Database, f.Clock, liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        var pilot = host.GetRequiredService<SqliteWorld>().FindPilot(1000001)!;
        var ask = market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && !o.Bid);
        Fund(f.Database, pilot.PilotId, ask.PriceCents * 1000);
        long initial = market.Balance(pilot.PilotId);
        market.Buy(pilot.PilotId, pilot.StationId, [new(Ammunition, ask.PriceCents, 20)]);
        var bid = market.Orders(pilot.PilotId, Ammunition).Single(o => o.StationId == pilot.StationId && o.Bid);
        long item;
        using (WorldDbContext world = new(f.Database.Path))
        {
            item = world.Set<OwnedItem>().Single(i => i.OwnerId == pilot.PilotId && i.TypeId == Ammunition).Id;
        }
        market.Sell(pilot.PilotId, [new(item, pilot.StationId, Ammunition, bid.PriceCents, 10)]);
        Assert.True(market.Balance(pilot.PilotId) < initial - ask.PriceCents * 10);
        // Controlled fixture transport isolates the economic margin. Gate travel
        // and cargo continuity are exercised by the encounter integration tests.
        const int hub = HubStation;
        using (WorldDbContext world = new(f.Database.Path))
        {
            world.Ships.Single(s => s.Id == pilot.ShipId).StationId = hub;
            world.Set<OwnedItem>().Single(i => i.Id == item).LocationId = hub;
            world.SaveChanges();
        }
        var destinationBid = market.Orders(pilot.PilotId, Ammunition).Single(o => o.Bid && o.StationId == hub);
        long beforeSale = market.Balance(pilot.PilotId);
        market.Sell(pilot.PilotId, [new(item, hub, Ammunition, destinationBid.PriceCents, 10)]);
        Assert.True(market.Balance(pilot.PilotId) > beforeSale);
        Assert.True(destinationBid.PriceCents > bid.PriceCents);
        Assert.True(destinationBid.Remaining > bid.Remaining);
    }

    [Fact]
    public void ExistingDevelopmentWorldCanSaveTheProgressiveMarketSwitch()
    {
        using TestWorld f = new(emptyWallets: false);
        WorldDatabase database = new(f.Path);
        TestClock clock = new();
        var configuration = new WorldConfigurationStore(database, clock);
        var before = configuration.Read();
        var settings = before.Active.Draft.Settings;
        Assert.True(settings.DevelopmentSupply.TestingMarket);
        configuration.Save(
            before.Generation,
            before.Active.Draft with
            {
                Settings = settings with { Economy = settings.Economy with { ProgressiveMarkets = true } },
            },
            "test"
        );
        WorldInitialization.Initialize(database, clock);
        Assert.True(database.Settings.Economy.ProgressiveMarkets);
        using WorldDbContext world = new(f.Path);
        Assert.True(world.Set<SavedWorldEconomy>().Single().TestingMarket);
    }

    private static CapsuleerHaulerTests.Fixture UnlockedFixture()
    {
        CapsuleerHaulerTests.Fixture fixture = new(legacyFounders: false);
        using (CatalogDbContext catalog = new(fixture.Database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{\"services\":[7,14]}}')"
            );
        }
        using WorldDbContext world = new(fixture.Database.Path);
        world.Add(
            new SavedMarketUnlock
            {
                TypeId = Ammunition,
                AccountId = 1000001,
                PilotId = 90000001,
                JobId = 1,
                ClaimedAt = fixture.Clock.Now,
            }
        );
        world.SaveChanges();
        return fixture;
    }

    private static void Fund(WorldDatabase database, long pilot, long cents)
    {
        using WorldDbContext world = new(database.Path);
        var wallet = world.Set<PilotWallet>().Find(pilot) ?? world.Add(new PilotWallet { PilotId = pilot }).Entity;
        wallet.Cents = cents;
        world.SaveChanges();
    }
}
