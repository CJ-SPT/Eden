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

public sealed class PlanetaryMarketTests
{
    private const long Pilot = 90000001;
    private const int Hub = 60003760;
    private const int OrdinaryStation = 60015157;
    private const int BarrenCommandCenter = 2524;
    private const long StartingBalance = 10_000_000_000;

    [Theory]
    [InlineData(2254)]
    [InlineData(2524)]
    [InlineData(2525)]
    [InlineData(2533)]
    [InlineData(2534)]
    [InlineData(2549)]
    [InlineData(2550)]
    [InlineData(2551)]
    public async Task HubCommandCentersCanBePurchasedAndSurviveCheckpointReopen(int typeId)
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
            var order = Assert.Single(market.Orders(Pilot, typeId), o => o.StationId == Hub && !o.Bid);
            orderId = order.Id;
            Assert.Equal(MarketSnapshotCatalog.Quotes[typeId].SellCents, order.PriceCents);
            Assert.Equal(NpcSupplyRules.Stock, order.Remaining);
            Assert.DoesNotContain(market.Orders(Pilot, typeId), o => o.StationId == OrdinaryStation && !o.Bid);
            Assert.Throws<InvalidDataException>(() => market.Buy(Pilot, Hub, [new(typeId, order.PriceCents - 1, 1)]));
            Assert.Equal(StartingBalance, market.Balance(Pilot));
            Assert.Empty(market.Transactions(Pilot));
            Assert.Equal(order, Assert.Single(market.Orders(Pilot, typeId), o => o.Id == orderId));

            var purchase = market.Buy(Pilot, Hub, [new(typeId, order.PriceCents, 1)]);
            balance = StartingBalance - order.PriceCents;
            Assert.Equal(balance, purchase.BalanceCents);
            var item = Assert.Single(purchase.Changes).Item;
            itemId = item.Id;
            Assert.Equal((Pilot, Hub, typeId, 1), (item.OwnerId, item.LocationId, item.TypeId, item.Quantity));
            Assert.Equal((int)InventoryFlag.Hangar, item.FlagId);
            Assert.Equal(1, Assert.Single(purchase.Trades).Quantity);
            Assert.Equal(NpcSupplyRules.Stock, Assert.Single(market.Orders(Pilot, typeId), o => o.Id == orderId).Remaining);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }

        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var reopenedMarket = reopened.GetRequiredService<IPilotMarket>();
        var savedOrder = Assert.Single(reopenedMarket.Orders(Pilot, typeId), o => o.StationId == Hub && !o.Bid);
        Assert.Equal(orderId, savedOrder.Id);
        Assert.Equal(NpcSupplyRules.Stock, savedOrder.Remaining);
        Assert.Equal(balance, reopenedMarket.Balance(Pilot));
        Assert.Single(reopenedMarket.Transactions(Pilot));
        using var action = reopened.GetRequiredService<LiveWorld>().Economy.Begin();
        var savedItem = action.Set<OwnedItem>().Find(itemId)!;
        Assert.Equal((Pilot, Hub, typeId, 1), (savedItem.OwnerId, savedItem.LocationId, savedItem.TypeId, savedItem.Quantity));
        Assert.Empty(action.Set<SavedMarketUnlock>());
    }

    [Fact]
    public void OriginalNpcSupplierStillStocksCommandCentersOutsideTheHub()
    {
        using TestWorld fixture = CreateWorld();
        WorldDatabase database = new(fixture.Path);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE Station SET OwnerId=1000010 WHERE Id=60015157");
        }
        using var host = ServerServices.Build(database, liveWorld: true);
        var orders = host.GetRequiredService<IPilotMarket>().Orders(Pilot, BarrenCommandCenter);
        Assert.Single(orders, o => o.StationId == Hub && !o.Bid);
        Assert.Single(orders, o => o.StationId == OrdinaryStation && !o.Bid);
    }

    [Theory]
    [InlineData("{\"published\":0,\"marketGroupID\":1322}", 0.333)]
    [InlineData("{\"published\":1}", 0.333)]
    [InlineData("{\"published\":1,\"marketGroupID\":1322}", -0.333)]
    [InlineData("{\"published\":1,\"marketGroupID\":1322}", 0)]
    public void HubSupplyRequiresPublishedMarketTypesAndAnNpcSellOffer(string definition, double trade)
    {
        using TestWorld fixture = CreateWorld();
        WorldDatabase database = new(fixture.Path);
        using (CatalogDbContext catalog = new(database.CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data={0} WHERE Dataset='client/types' AND Key='2524'", definition);
            string corporation = System.Text.Json.JsonSerializer.Serialize(
                new
                {
                    corporationTrades = new Dictionary<string, double>
                    {
                        [BarrenCommandCenter.ToString(System.Globalization.CultureInfo.InvariantCulture)] = trade,
                    },
                }
            );
            catalog.Database.ExecuteSqlRaw(
                "UPDATE SourceRecord SET Data={0} WHERE Dataset='client/npccorporations' AND Key='1000010'",
                corporation
            );
        }
        using var host = ServerServices.Build(database, liveWorld: true);
        Assert.DoesNotContain(host.GetRequiredService<IPilotMarket>().Orders(Pilot, BarrenCommandCenter), o => !o.Bid);
    }

    private static TestWorld CreateWorld()
    {
        TestWorld fixture = new();
        PassiveCatalogFixture.LoadPlanetary(new WorldDatabase(fixture.Path).CatalogPath);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
        {
            // Build 3662168: group 1027 has eight published types. Kaalakiota's
            // fractional positive corporationTrades values designate NPC supply.
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/stationoperations','14','{{"services":[7,14]}}'),
                    ('client/systems','30000142','{{"securityStatus":1.0}}'),
                    ('client/npccorporations','1000035','{{"corporationTrades":{{}}}}'),
                    ('client/npccorporations','1000010',
                        '{{"corporationTrades":{{"2254":0.333,"2524":0.333,"2525":0.333,"2533":0.333,
                            "2534":0.333,"2549":0.333,"2550":0.333,"2551":0.333}}}}');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60015157,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                INSERT INTO NpcCorporation(Id,Name) VALUES(1000010,'Kaalakiota Corporation');
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
