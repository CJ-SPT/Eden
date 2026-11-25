using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.PilotSessions;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class RangedMarketTests
{
    private const long BuyerAccount = 1000002;
    private const int Ammo = 215;
    private const int NeighborStation = 60003761;

    [Fact]
    public async Task NativeRangedBidFillsFromTheSellerStationAndKeepsItsRangeAcrossCheckpoint()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        var live = Prepare(fixture);
        var seller = fixture.Pilot;
        var buyer = fixture.Db.Store.FindPilot(BuyerAccount)!;
        using (CourierServiceTests.Client client = new(fixture.Db, fixture.Clock, BuyerAccount))
        {
            client.Refresh();
            Assert.Same(PilotCallResult.None, Place(client, buyer.StationId, 12d, 5, range: 0));
            Assert.Same(PilotCallResult.None, Place(client, buyer.StationId, 11d, 5, range: 1, minimum: 2));
        }
        long stackId = StageSeller(live, seller.PilotId, seller.ShipId);
        Assert.Throws<InvalidDataException>(() => fixture.Market.Sell(seller.PilotId, [new(stackId, NeighborStation, Ammo, 1000, 1)]));
        var sale = fixture.Market.Sell(seller.PilotId, [new(stackId, NeighborStation, Ammo, 1000, 3)]);
        Assert.Equal(3, Assert.Single(sale.Trades).Quantity);
        var orders = fixture.Market.PersonalOrders(buyer.PilotId);
        var ranged = Assert.Single(orders, order => order.Range == 1);
        Assert.Equal(5, Assert.Single(orders, order => order.Range == 0).Remaining);
        Assert.Equal(2, ranged.Remaining);
        Assert.Equal(2200, ranged.EscrowCents);
        CheckDelivery(live, buyer.PilotId, expected: 3);

        await live.FlushAsync();
        fixture.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(fixture.Db.Path), fixture.Clock, liveWorld: true);
        var restored = reopened.GetRequiredService<IPersonalMarket>();
        Assert.Contains(ranged, restored.PersonalOrders(buyer.PilotId));
        Assert.Contains(ranged, reopened.GetRequiredService<IPilotMarket>().Orders(buyer.PilotId, Ammo));
        CheckDelivery(reopened.GetRequiredService<LiveWorld>(), buyer.PilotId, expected: 3);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromMinutes(5));
        var change = fixture.Modification(ranged, 1150) with { Range = ranged.Range };
        Assert.Throws<InvalidDataException>(() => restored.Modify(buyer.PilotId, change with { Range = 0 }));
        restored.Modify(buyer.PilotId, change);
        Assert.Equal(1150, Assert.Single(restored.PersonalOrders(buyer.PilotId), order => order.Range == 1).PriceCents);
    }

    [Fact]
    public void VisibilityGatesRemoteRangeAndExistingRegionalAsksFillAtTheirStation()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        var live = Prepare(fixture);
        var buyer = fixture.Db.Store.FindPilot(BuyerAccount)!;
        var seller = fixture.Pilot;
        _ = StageSeller(live, seller.PilotId, seller.ShipId);
        fixture.List(3, price: 900);
        Train(fixture, live, buyer.PilotId, MarketSkill.Procurement, level: 1);
        fixture.Db.Store.SetPosition(buyer.ShipId, SpacePosition.Zero, buyer.SystemId);
        using CourierServiceTests.Client client = new(fixture.Db, fixture.Clock, BuyerAccount);
        client.Refresh();
        long balance = fixture.Market.Balance(buyer.PilotId);
        var failure = Assert.IsType<PilotCallResult.Failure>(Place(client, buyer.StationId, 10d, 5, range: 1));
        Assert.Contains("Visibility", failure.Message);
        Assert.Equal(balance, fixture.Market.Balance(buyer.PilotId));
        Assert.Empty(fixture.Market.PersonalOrders(buyer.PilotId));
        Train(fixture, live, buyer.PilotId, MarketSkill.Visibility, level: 2);
        Assert.IsType<PilotCallResult.Failure>(Place(client, buyer.StationId, 10d, 5, range: 10));
        Assert.IsType<PilotCallResult.Failure>(Place(client, buyer.StationId, 10d, 5, range: 6));
        Assert.Same(PilotCallResult.None, Place(client, buyer.StationId, 10d, 5, range: 1));
        var matched = Assert.Single(fixture.Market.Transactions(buyer.PilotId));
        Assert.Equal(NeighborStation, matched.StationId);
        Assert.Equal(3, matched.Quantity);
        var bid = fixture.Order(buyer.PilotId);
        Assert.Equal(buyer.StationId, bid.StationId);
        Assert.Equal(2, bid.Remaining);
        Assert.Equal(1, bid.Range);
        Assert.Empty(fixture.Market.PersonalOrders(seller.PilotId));
        CheckDelivery(live, buyer.PilotId, expected: 3);
    }

    [Fact]
    public async Task Version233BuyOrdersKeepTheirStationRangeAfterUpgrade()
    {
        using PersonalMarketTests.Fixture fixture = new(live: true);
        var buyer = fixture.Db.Store.FindPilot(BuyerAccount)!;
        fixture.Market.PlaceBuy(buyer.PilotId, new(buyer.StationId, Ammo, 1000, 5, 1));
        var prior = fixture.Order(buyer.PilotId);
        await fixture.Db.Services.GetRequiredService<LiveWorld>().FlushAsync();
        fixture.Db.Services.Dispose();
        using (WorldDbContext world = new(fixture.Db.Path))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPersonalOrder DROP COLUMN Range; PRAGMA user_version=233;");
        }
        WorldDatabase database = new(fixture.Db.Path);
        WorldInitialization.Initialize(database);
        using var reopened = ServerServices.Build(database, fixture.Clock, liveWorld: true);
        Assert.Equal(prior, Assert.Single(reopened.GetRequiredService<IPersonalMarket>().PersonalOrders(buyer.PilotId)));
    }

    private static PilotCallResult Place(
        CourierServiceTests.Client client,
        int station,
        double price,
        long quantity,
        long range,
        long minimum = 1
    ) => client.Call("PlaceBuyOrder", [(long)station, (long)Ammo, price, quantity, range, minimum, 1L, false, null], "marketProxy");

    private static LiveWorld Prepare(PersonalMarketTests.Fixture fixture)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                SELECT 60003761,TypeId,OwnerId,30000144,OperationId,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
            """
        );
        return fixture.Db.Services.GetRequiredService<LiveWorld>();
    }

    private static long StageSeller(LiveWorld live, long pilotId, long shipId)
    {
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        world.Ships.Find(shipId)!.StationId = NeighborStation;
        var stack = world.Filter<OwnedItem>(item => item.OwnerId == pilotId && item.TypeId == Ammo).Single();
        stack.LocationId = NeighborStation;
        world.Commit();
        return stack.Id;
    }

    private static void CheckDelivery(LiveWorld live, long pilotId, int expected)
    {
        var delivered = live.Economy.Inspect(world =>
            world.Filter<OwnedItem>(item => item.OwnerId == pilotId && item.TypeId == Ammo).Single()
        );
        Assert.Equal(NeighborStation, delivered.LocationId);
        Assert.Equal(expected, delivered.Quantity);
    }

    private static void Train(PersonalMarketTests.Fixture fixture, LiveWorld live, long pilotId, MarketSkill skill, int level)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath);
        double rank = catalog.RawFittingAttributes((int)skill).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant, 1);
        using var world = live.Economy.Begin(new(pilots: [pilotId]));
        world.Set<Account>().Find(BuyerAccount)!.CloneGrade = AccountCloneGrade.Omega;
        world.Add(
            new PilotSkill
            {
                PilotId = pilotId,
                TypeId = (int)skill,
                Points = TrainedSkill.PointsForLevel(rank, level),
            }
        );
        world.Commit();
    }
}
