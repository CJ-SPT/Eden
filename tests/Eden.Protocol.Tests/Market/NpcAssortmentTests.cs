using System.Reflection;
using System.Text.Json;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class NpcAssortmentTests
{
    private const long Pilot = 90000001;
    private const int Hub = 60003760;
    private const int OrdinaryStation = 60015157;
    private const long StartingBalance = 10_000_000_000_000;

    [Fact]
    public async Task DisablingGlobalSupplyPreservesOriginalNpcTradeGoodsAndPurchasedItems()
    {
        using TestWorld fixture = CreateWorld();
        WorldDatabase database = new(fixture.Path);
        long balance;
        using (var host = ServerServices.Build(database, liveWorld: true))
        {
            var market = host.GetRequiredService<IPilotMarket>();
            var core = Assert.Single(market.Orders(Pilot, 56207), order => order.StationId == Hub && !order.Bid);
            balance = market.Buy(Pilot, Hub, [new(core.TypeId, core.PriceCents, 1)]).BalanceCents;
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        var config = new Eden.World.Configuration.WorldConfigurationStore(database, TimeProvider.System);
        var state = config.Read();
        config.Save(
            state.Generation,
            state.Active.Draft with
            {
                Settings = state.Active.Draft.Settings with
                {
                    Economy = state.Active.Draft.Settings.Economy with { RegionalHubSupply = false },
                    DevelopmentSupply = state.Active.Draft.Settings.DevelopmentSupply with { TestingMarket = false },
                },
            },
            "admin"
        );
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, liveWorld: true);
        var shop = reopened.GetRequiredService<IPilotMarket>();
        Assert.DoesNotContain(shop.Orders(Pilot, 56207), order => !order.Bid);
        Assert.Contains(shop.Orders(Pilot, 3687), order => order.StationId == Hub && !order.Bid);
        Assert.Equal(balance, shop.Balance(Pilot));
        Assert.Single(shop.Transactions(Pilot));
        using var saved = reopened.GetRequiredService<LiveWorld>().Economy.Begin();
        Assert.Single(saved.Set<OwnedItem>(), item => item.OwnerId == Pilot && item.TypeId == 56207 && item.LocationId == Hub);
    }

    [Fact]
    public void EveryPublishedNpcSellTypeIsAvailableWithoutProductionUnlocks()
    {
        using TestWorld fixture = CreateWorld();
        using var host = ServerServices.Build(new(fixture.Path), liveWorld: true);
        var market = host.GetRequiredService<IPilotMarket>();
        var orders = market.Orders(Pilot);
        using var action = host.GetRequiredService<LiveWorld>().Economy.Begin();
        var supplied = action
            .Set<NpcMarketOrder>()
            .Where(order => order.StationId == Hub && order.Kind == NpcMarketKind.Institutional && !order.Bid && order.Remaining > 0)
            .Select(order => order.TypeId)
            .Order()
            .ToArray();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Eden.Protocol.Tests.Fixtures.NpcSupplyCatalog.json")!;
        using var audit = JsonDocument.Parse(stream);
        var expected = audit.RootElement.GetProperty("expectedSellTypes").EnumerateArray().Select(value => value.GetInt32()).ToArray();
        Assert.Equal(2394, audit.RootElement.GetProperty("npcSellTypes").GetArrayLength());
        Assert.Equal(2308, expected.Length);
        Assert.Equal(expected, supplied);
        Assert.All(supplied, type => Assert.Contains(orders, order => order.TypeId == type && order.StationId == Hub && !order.Bid));
        Assert.Empty(action.Set<SavedMarketUnlock>());
    }

    [Theory]
    [InlineData(3687)] // Electronic Parts: snapshot quote, commodity.
    [InlineData(3812)] // Data Sheets: another NPC-only industry input.
    [InlineData(3643)] // Soil: no snapshot sell quote.
    [InlineData(3412)] // Astrometrics: no snapshot sell quote, skillbook.
    [InlineData(56207)] // Keepstar Upwell Quantum Core: no snapshot sell quote.
    [InlineData(37346)] // NPC-sold Tech I rig original with an inconsistent blueprint tech tag.
    public async Task SeededGoodsCanBeBoughtAndSurviveCheckpointReopen(int typeId)
    {
        using TestWorld fixture = CreateWorld();
        WorldDatabase database = new(fixture.Path);
        TestClock clock = new();
        long orderId;
        long itemId;
        long balance;
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var market = host.GetRequiredService<IPilotMarket>();
            var order = Assert.Single(market.Orders(Pilot, typeId), order => order.StationId == Hub && !order.Bid);
            orderId = order.Id;
            using CatalogDbContext catalog = new(database.CatalogPath);
            long expectedPrice =
                MarketSnapshotCatalog.Quotes.GetValueOrDefault(typeId)?.SellCents
                ?? (long)(catalog.Read(CatalogData.Client.Types, (long)typeId)!.BasePrice!.Value * MarketDefinitions.CentsPerIsk);
            Assert.Equal(expectedPrice, order.PriceCents);
            Assert.DoesNotContain(market.Orders(Pilot, typeId), order => order.StationId == OrdinaryStation && !order.Bid);
            Assert.Throws<InvalidDataException>(() => market.Buy(Pilot, Hub, [new(typeId, order.PriceCents - 1, 1)]));
            Assert.Equal(StartingBalance, market.Balance(Pilot));
            Assert.Empty(market.Transactions(Pilot));
            Assert.Equal(order, Assert.Single(market.Orders(Pilot, typeId), candidate => candidate.Id == orderId));
            var purchase = market.Buy(Pilot, Hub, [new(typeId, order.PriceCents, 1)]);
            balance = StartingBalance - order.PriceCents;
            Assert.Equal(balance, purchase.BalanceCents);
            var item = Assert.Single(purchase.Changes).Item;
            itemId = item.Id;
            Assert.Equal(
                (Pilot, Hub, typeId, 1, (int)InventoryFlag.Hangar),
                (item.OwnerId, item.LocationId, item.TypeId, item.Quantity, item.FlagId)
            );
            Assert.Equal(1, Assert.Single(purchase.Trades).Quantity);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var reopenedMarket = reopened.GetRequiredService<IPilotMarket>();
        Assert.Equal(orderId, Assert.Single(reopenedMarket.Orders(Pilot, typeId), order => order.StationId == Hub && !order.Bid).Id);
        Assert.Equal(balance, reopenedMarket.Balance(Pilot));
        Assert.Single(reopenedMarket.Transactions(Pilot));
        using var saved = reopened.GetRequiredService<LiveWorld>().Economy.Begin();
        var savedItem = saved.Set<OwnedItem>().Find(itemId)!;
        Assert.Equal((Pilot, Hub, typeId, 1), (savedItem.OwnerId, savedItem.LocationId, savedItem.TypeId, savedItem.Quantity));
        Assert.Empty(saved.Set<SavedMarketUnlock>());
    }

    private static TestWorld CreateWorld()
    {
        TestWorld fixture = new();
        WorldDatabase database = new(fixture.Path);
        PassiveCatalogFixture.LoadNpcSupply(database.CatalogPath);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/stationoperations','14','{{"services":[7,14]}}'),
                    ('client/systems','30000142','{{"securityStatus":1.0}}'),
                    ('client/npccorporations','1999999','{{"corporationTrades":{{}}}}');
                INSERT INTO NpcCorporation(Id,Name) VALUES(1999999,'No NPC offers');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60015157,TypeId,1999999,SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
        }
        using WorldDbContext world = new(fixture.Path);
        world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
        world.Ships.Single(ship => ship.Id == world.Pilots.Single(pilot => pilot.Id == Pilot).ActiveShipId).StationId = OrdinaryStation;
        world.Add(new PilotWallet { PilotId = Pilot, Cents = StartingBalance });
        world.SaveChanges();
        return fixture;
    }
}
