using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Gameplay.Market;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Market;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class BlueprintMarketTests
{
    private const long Pilot = 90000001;
    private const int Hub = 60003760;
    private const int SecondHub = 60008494;
    private const int OrdinaryStation = 60015157;
    private const int MinerBlueprint = 785;
    private const int VentureBlueprint = 32881;

    [Theory]
    [InlineData(Hub)]
    [InlineData(SecondHub)]
    public void EveryRegionalHubStocksTheGlobalNpcBlueprintSellAssortment(int station)
    {
        using TestWorld fixture = CreateWorld(station);
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        var orders = market.Orders(Pilot);
        foreach (int type in new[] { MinerBlueprint, VentureBlueprint })
        {
            var order = Assert.Single(orders, o => o.StationId == station && o.TypeId == type && !o.Bid);
            Assert.Equal(MarketSnapshotCatalog.Quotes[type].SellCents, order.PriceCents);
            Assert.Equal(NpcSupplyRules.Stock, order.Remaining);
            Assert.Equal(1000035, order.OwnerId);
        }
        Assert.Contains(orders, o => o.TypeId == MinerBlueprint && o.Bid && o.StationId == station);
        Assert.DoesNotContain(orders, o => o.TypeId is 786 or 1130 or 1131 or 43 or 999999);
        Assert.Equal(orders, market.Orders(Pilot));
        using WorldDbContext world = new(fixture.Path);
        Assert.Empty(world.Set<SavedMarketUnlock>());
        Assert.Empty(world.Set<MarketExecution>());
    }

    [Fact]
    public void IndustrialOriginalsUseCatalogPricesAndMaterialsStayOutOfNpcSupply()
    {
        const int athanorBlueprint = 36977;
        const int moonMaterial = 16633;
        const int molecularCondenser = 57445;
        using TestWorld fixture = CreateWorld(Hub);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES(4,'Material'),(17,'Commodity');
                INSERT OR IGNORE INTO ItemGroup VALUES(427,'Moon Materials',4),(526,'Commodities',17);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId)
                VALUES(36977,'Athanor Blueprint',134),(16633,'Hydrocarbons',427),(57445,'Molecular Condenser',526);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/types','36977','{{"published":1,"marketGroupID":1,"basePrice":5500000000}}'),
                    ('client/types','16633','{{"published":1,"marketGroupID":1,"volume":1}}'),
                    ('client/types','57445','{{"published":1,"marketGroupID":1,"volume":1}}'),
                    ('client/npccorporations','1000035','{{"corporationTrades":{{"36977":1,"16633":1,"57445":1}}}}');
                """
            );
        }
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        Assert.Null(MarketSnapshotCatalog.Quotes[athanorBlueprint].SellCents);
        Assert.NotNull(MarketSnapshotCatalog.Quotes[moonMaterial].SellCents);
        var original = Assert.Single(market.Orders(Pilot, athanorBlueprint), o => o.StationId == Hub && !o.Bid);
        Assert.Equal(550_000_000_000L, original.PriceCents);
        Assert.DoesNotContain(market.Orders(Pilot, moonMaterial), o => !o.Bid);
        Assert.DoesNotContain(market.Orders(Pilot, molecularCondenser), o => !o.Bid);
    }

    [Fact]
    public void SnapshotlessCatalogOriginalKeepsItsNpcSupplierWithoutSeedingUnlistedOrInventedBlueprints()
    {
        const int Offered = 691;
        const int Unlisted = 939;
        const int Invented = 784;
        using TestWorld fixture = CreateWorld(Hub);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES
                    (691,'Rifter Blueprint',134),(939,'Navitas Blueprint',134),(784,'Miner II Blueprint',134),(482,'Miner II',54);
                INSERT OR IGNORE INTO DogmaAttribute VALUES (422,'techLevel');
                INSERT OR REPLACE INTO TypeAttribute VALUES (482,422,2);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/types','691','{{"published":1,"marketGroupID":1,"basePrice":2800000}}'),
                    ('client/types','939','{{"published":1,"marketGroupID":1,"basePrice":2525000}}'),
                    ('client/types','784','{{"published":1,"marketGroupID":1,"basePrice":5000000}}'),
                    ('client/blueprints','"691"','{{"blueprintTypeID":691,"activities":{{"manufacturing":
                        {{"products":[{{"typeID":601,"quantity":1}}]}}}}}}'),
                    ('client/blueprints','"939"','{{"blueprintTypeID":939,"activities":{{"manufacturing":
                        {{"products":[{{"typeID":601,"quantity":1}}]}}}}}}'),
                    ('client/blueprints','"784"','{{"blueprintTypeID":784,"activities":{{"manufacturing":
                        {{"products":[{{"typeID":482,"quantity":1}}]}}}}}}'),
                    ('client/npccorporations','1000035','{{"corporationTrades":{{"691":1,"784":1}}}}');
                """
            );
        }
        var market = fixture.Services.GetRequiredService<IPilotMarket>();
        Assert.Equal(280_000_000, Assert.Single(market.Orders(Pilot, Offered), order => !order.Bid).PriceCents);
        Assert.DoesNotContain(market.Orders(Pilot, Unlisted), order => !order.Bid);
        Assert.DoesNotContain(market.Orders(Pilot, Invented), order => !order.Bid);
    }

    [Fact]
    public void OrdinaryStationsKeepLocalSuppliersWhileRemoteHubBlueprintsAreVisible()
    {
        using TestWorld fixture = CreateWorld(OrdinaryStation);
        var orders = fixture.Services.GetRequiredService<IPilotMarket>().Orders(Pilot);
        Assert.Contains(orders, o => o.StationId == Hub && o.TypeId == VentureBlueprint && !o.Bid);
        Assert.DoesNotContain(orders, o => o.StationId == OrdinaryStation && !o.Bid && o.TypeId is MinerBlueprint or VentureBlueprint);
        Assert.Contains(orders, o => o.StationId == OrdinaryStation && o.TypeId == MinerBlueprint && o.Bid);
    }

    [Theory]
    [InlineData("{\"published\":0,\"marketGroupID\":1}")]
    [InlineData("{\"published\":1}")]
    public void GlobalBlueprintOffersStillRequirePublishedMarketTypes(string definition)
    {
        using TestWorld fixture = CreateWorld(Hub);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("UPDATE SourceRecord SET Data={0} WHERE Dataset='client/types' AND Key='32881'", definition);
        }
        var orders = fixture.Services.GetRequiredService<IPilotMarket>().Orders(Pilot);
        Assert.DoesNotContain(orders, o => o.TypeId == VentureBlueprint);
        Assert.Contains(orders, o => o.TypeId == MinerBlueprint && !o.Bid);
    }

    [Fact]
    public async Task ExistingHubOrdersAreBackfilledAndPurchasedOriginalsSurviveReopen()
    {
        TestClock clock = new();
        using TestWorld fixture = CreateWorld(OrdinaryStation, clock);
        WorldDatabase database = new(fixture.Path);
        long existingOrder;
        using (WorldDbContext world = new(fixture.Path))
        {
            var oldOrder = world
                .Add(
                    new NpcMarketOrder
                    {
                        Kind = NpcMarketKind.Institutional,
                        StationId = Hub,
                        SystemId = 30000142,
                        RegionId = 10000002,
                        OwnerId = 1000035,
                        TypeId = MinerBlueprint,
                        PriceCents = 1,
                        Entered = NpcSupplyRules.Stock,
                        Remaining = NpcSupplyRules.Stock,
                        Issued = clock.Now,
                    }
                )
                .Entity;
            world.Add(new PilotWallet { PilotId = Pilot, Cents = 10_000_000_000L });
            world.SaveChanges();
            existingOrder = oldOrder.Id;
        }
        long balance;
        long item;
        long boughtOrder;
        using (var host = ServerServices.Build(database, clock, liveWorld: true))
        {
            var market = host.GetRequiredService<IPilotMarket>();
            var orders = market.Orders(Pilot);
            Assert.Equal(existingOrder, Assert.Single(orders, o => o.TypeId == MinerBlueprint && !o.Bid).Id);
            var blueprint = Assert.Single(orders, o => o.TypeId == VentureBlueprint && !o.Bid);
            boughtOrder = blueprint.Id;
            long before = market.Balance(Pilot);
            Assert.Throws<InvalidDataException>(() => market.Buy(Pilot, SecondHub, [new(VentureBlueprint, blueprint.PriceCents, 1)]));
            Assert.Equal(before, market.Balance(Pilot));
            Assert.Empty(market.Transactions(Pilot));
            var purchase = market.Buy(Pilot, Hub, [new(VentureBlueprint, blueprint.PriceCents, 1)]);
            balance = purchase.BalanceCents;
            Assert.Equal(before - blueprint.PriceCents, balance);
            var delivered = Assert.Single(purchase.Changes).Item;
            Assert.Equal(Hub, delivered.LocationId);
            Assert.Equal(VentureBlueprint, delivered.TypeId);
            Assert.Equal(1, delivered.Quantity);
            item = delivered.Id;
            Assert.Equal(1, Assert.Single(purchase.Trades).Quantity);
            await host.GetRequiredService<LiveWorld>().FlushAsync();
        }
        WorldInitialization.Initialize(database, clock);
        using var reopened = ServerServices.Build(database, clock, liveWorld: true);
        var reopenedMarket = reopened.GetRequiredService<IPilotMarket>();
        var asks = reopenedMarket.Orders(Pilot).Where(o => !o.Bid).ToArray();
        Assert.Equal(existingOrder, Assert.Single(asks, o => o.TypeId == MinerBlueprint).Id);
        Assert.Equal(boughtOrder, Assert.Single(asks, o => o.TypeId == VentureBlueprint).Id);
        Assert.Equal(balance, reopenedMarket.Balance(Pilot));
        Assert.Single(reopenedMarket.Transactions(Pilot));
        using WorldDbContext saved = new(fixture.Path);
        var original = saved.Set<OwnedItem>().Single(i => i.Id == item);
        Assert.Equal((Pilot, Hub, VentureBlueprint, 1), (original.OwnerId, original.LocationId, original.TypeId, original.Quantity));
        Assert.Equal(-1, original.BlueprintRuns);
        Assert.Equal(0, original.MaterialEfficiency);
        Assert.Equal(0, original.TimeEfficiency);
    }

    private static TestWorld CreateWorld(int station, TestClock? clock = null)
    {
        TestWorld fixture = new(clock: clock);
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory VALUES(9,'Blueprint'),(17,'Commodity');
                INSERT OR IGNORE INTO ItemGroup VALUES(134,'Blueprint',9),(280,'Commodity',17),(25,'Frigate',6);
                INSERT OR IGNORE INTO ItemType(Id,Name,GroupId) VALUES(785,'Miner I Blueprint',134),
                    (32881,'Venture Blueprint',134),(786,'Buy-only Blueprint',134),(1130,'Tech II Blueprint',134),
                    (1131,'Advanced Product Blueprint',134),(999999,'Unquoted Blueprint',134),(43,'Antibiotics',280),(603,'Merlin',25);
                INSERT OR IGNORE INTO DogmaAttribute(Id,Name) VALUES(422,'techLevel');
                INSERT OR REPLACE INTO TypeAttribute VALUES(1130,422,2),(603,422,3);
                INSERT OR REPLACE INTO SourceRecord VALUES
                    ('client/stationoperations','14','{{"services":[7,14]}}'),
                    ('client/systems','30000141','{{"securityStatus":1.0}}'),
                    ('client/systems','30000142','{{"securityStatus":1.0}}'),
                    ('client/systems','30002187','{{"securityStatus":1.0}}'),
                    ('client/types','785','{{"published":1,"marketGroupID":1}}'),
                    ('client/types','32881','{{"published":1,"marketGroupID":1,"volume":0.01}}'),
                    ('client/types','786','{{"published":1,"marketGroupID":1}}'),
                    ('client/types','1130','{{"published":1,"marketGroupID":1}}'),
                    ('client/types','1131','{{"published":1,"marketGroupID":1}}'),
                    ('client/types','999999','{{"published":1,"marketGroupID":1}}'),
                    ('client/types','43','{{"published":1,"marketGroupID":1}}'),
                    ('client/blueprints','"1131"','{{"activities":{{"manufacturing":{{"products":[{{"typeID":603,"quantity":1}}]}}}}}}'),
                    ('client/npccorporations','1000035','{{"corporationTrades":{{"785":-1}}}}'),
                    ('client/npccorporations','1000125',
                        '{{"corporationTrades":{{"785":1,"32881":1,"786":-1,"1130":1,"1131":1,"999999":1,"43":1}}}}');
                INSERT INTO Region(Id,Name) VALUES(10000043,'Second region');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES(20000998,10000043,'Second');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES(30002187,20000998,'Second hub');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60008494,TypeId,OwnerId,30002187,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60015157,TypeId,OwnerId,30000142,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                """
            );
        }
        using WorldDbContext world = new(fixture.Path);
        world.Set<SavedWorldEconomy>().Single().TestingMarket = false;
        world.Ships.Single(ship => ship.Id == world.Pilots.Single(pilot => pilot.Id == Pilot).ActiveShipId).StationId = station;
        world.SaveChanges();
        return fixture;
    }
}
