using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Structures;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Structures;
using Eden.Protocol.Market;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Market;

public sealed class RemoteMarketTests
{
    private const long AccountId = 1000001;
    private const int SellingStation = 60003761;
    private const int ForeignStation = 60003762;
    private const int ForeignSystem = 30009999;
    private const int ForeignRegion = 10000999;
    private const int HomeSystem = 30000142;
    private const int NeighborSystem = 30000144;
    private const int HomeRegion = 10000002;
    private const int ItemType = 3651;
    private const long UnitPrice = 100;
    private const long StructureId = 1100000000000;

    private sealed class Fixture : IDisposable
    {
        internal TestWorld Db { get; } = new(emptyWallets: false);
        internal IPilotMarket Market => Db.Services.GetRequiredService<IPilotMarket>();

        internal Fixture(int sellingSystem = NeighborSystem)
        {
            using CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true);
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO Region(Id,Name) VALUES (10000999,'Other region');
                INSERT INTO Constellation(Id,RegionId,Name) VALUES (20009999,10000999,'Other constellation');
                INSERT INTO SolarSystem(Id,ConstellationId,Name) VALUES (30009999,20009999,'Other system');
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60003761,TypeId,OwnerId,30000144,15,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                INSERT INTO Station(Id,TypeId,OwnerId,SolarSystemId,OperationId,OrbitId,X,Y,Z)
                    SELECT 60003762,TypeId,OwnerId,30009999,15,OrbitId,X,Y,Z FROM Station WHERE Id=60003760;
                INSERT OR REPLACE INTO SourceRecord VALUES ('client/stationoperations','14','{{"services":[]}}'),
                    ('client/stationoperations','15','{{"services":[7]}}');
                """
            );
            catalog.Database.ExecuteSql($"UPDATE Station SET SolarSystemId={sellingSystem} WHERE Id={SellingStation}");
            using WorldDbContext world = new(Db.Path);
            world.Set<OwnedItem>().Where(i => i.TypeId == ItemType).ExecuteDelete();
        }

        public void Dispose() => Db.Dispose();
    }

    [Theory]
    [InlineData("PlaceBuyOrder", false, HomeSystem)]
    [InlineData("PlaceBuyOrder", true, HomeSystem)]
    [InlineData("PlaceBuyOrder", false, NeighborSystem)]
    [InlineData("PlaceBuyOrder", true, NeighborSystem)]
    [InlineData("BuyMultipleItems", false, HomeSystem)]
    [InlineData("BuyMultipleItems", true, HomeSystem)]
    [InlineData("BuyMultipleItems", false, NeighborSystem)]
    [InlineData("BuyMultipleItems", true, NeighborSystem)]
    public void RegionalPurchasesDeliverToSellingStationAndNotifyItsLocation(string method, bool inSpace, int sellingSystem)
    {
        using Fixture f = new(sellingSystem);
        var pilot = f.Db.Store.FindPilot(AccountId)!;
        if (inSpace)
        {
            f.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, HomeSystem);
        }
        long before = f.Market.Balance(pilot.PilotId);
        using var scope = f.Db.Services.CreateScope();
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        using PilotCallContext context = new(
            AccountId,
            pilot.Account,
            [pilot],
            p => new(f.Db.Store, p, TimeProvider.System, new(), f.Db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        object?[] args =
            method == "PlaceBuyOrder"
                ? [(long)SellingStation, (long)ItemType, 1d, 2L, (long)MarketDefinitions.StationRange, 1L, 0L, false, null]
                :
                [
                    (long)SellingStation,
                    new List<object?>
                    {
                        new BlueInstance(
                            "utillib.KeyVal",
                            new Dictionary<string, object?>
                            {
                                ["typeID"] = (long)ItemType,
                                ["price"] = 1d,
                                ["quantity"] = 2L,
                            }
                        ),
                    },
                    false,
                ];
        var result = router.CallWire(context, "marketProxy", method, args, _ => { });
        if (method == "PlaceBuyOrder")
        {
            Assert.Null(result);
        }
        else
        {
            Assert.Single(Assert.IsType<List<object?>>(result));
        }
        Assert.Equal(before - UnitPrice * 2, f.Market.Balance(pilot.PilotId));
        var changed = Assert.IsType<OwnOrdersChanged>(Assert.Single(context.Notifications, n => n.Name == "OnOwnOrdersChanged"));
        var order = Assert.Single(changed.Orders).Order;
        Assert.Equal(SellingStation, order.StationId);
        Assert.Equal(sellingSystem, order.SystemId);
        Assert.Equal(HomeRegion, order.RegionId);
        var received = Assert.IsType<MarketItemReceived>(Assert.Single(context.Notifications, n => n.Name == "OnMarketItemReceived"));
        Assert.Equal(SellingStation, received.Item.LocationId);
        Assert.Single(context.Notifications, n => n.Name == "OnItemChange");
        Assert.Single(context.Notifications, n => n.Name == "OnAccountChange");
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        var item = Assert.Single(saved.Set<OwnedItem>(), i => i.OwnerId == pilot.PilotId && i.TypeId == ItemType);
        Assert.Equal(SellingStation, item.LocationId);
        Assert.Equal((int)InventoryFlag.Hangar, item.FlagId);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(SellingStation, Assert.Single(f.Market.Transactions(pilot.PilotId)).StationId);
        Assert.Equal(before - UnitPrice * 2, f.Market.Balance(pilot.PilotId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossRegionPurchasesUseCurrentPositionAndLeaveAssetsUnchanged(bool inSpace)
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(AccountId)!;
        int destination = ForeignStation;
        if (inSpace)
        {
            // The last station is in The Forge; the actual ship is now in another region.
            f.Db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero, ForeignSystem);
            destination = SellingStation;
        }
        long before = f.Market.Balance(pilot.PilotId);
        var error = Assert.Throws<InvalidDataException>(() => f.Market.Buy(pilot.PilotId, destination, [new(ItemType, UnitPrice, 1)]));
        Assert.Contains("current region", error.Message);
        Assert.Equal(before, f.Market.Balance(pilot.PilotId));
        Assert.Empty(f.Market.Transactions(pilot.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.OwnerId == pilot.PilotId && i.TypeId == ItemType));
        Assert.Empty(saved.Set<NpcMarketOrder>());
        Assert.Empty(saved.Set<SavedPersonalOrder>());
        if (inSpace)
        {
            var trade = f.Market.Buy(pilot.PilotId, ForeignStation, [new(ItemType, UnitPrice, 1)]);
            Assert.Equal(ForeignRegion, trade.RegionId);
            Assert.Equal(ForeignSystem, trade.SystemId);
        }
    }

    [Fact]
    public void DockedStructureUsesItsRegionInsteadOfLastStation()
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(AccountId)!;
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new SavedStructure
                {
                    Id = StructureId,
                    SystemId = ForeignSystem,
                    State = StructureState.ShieldVulnerable,
                }
            );
            world.Ships.Find(pilot.ShipId)!.StructureId = StructureId;
            world.SaveChanges();
        }
        long before = f.Market.Balance(pilot.PilotId);
        var error = Assert.Throws<InvalidDataException>(() => f.Market.Buy(pilot.PilotId, SellingStation, [new(ItemType, UnitPrice, 1)]));
        Assert.Contains("current region", error.Message);
        Assert.Equal(before, f.Market.Balance(pilot.PilotId));
        Assert.Empty(f.Market.Transactions(pilot.PilotId));
        var purchase = f.Market.Buy(pilot.PilotId, ForeignStation, [new(ItemType, UnitPrice, 1)]);
        Assert.Equal(before - UnitPrice, purchase.BalanceCents);
        Assert.Equal(ForeignStation, Assert.Single(purchase.Changes).Item.LocationId);
        Assert.Equal(ForeignRegion, purchase.RegionId);
    }

    [Fact]
    public void FailedRemoteMultibuyRollsBackAndRestingOrdersStillRequireDocking()
    {
        using Fixture f = new();
        var pilot = f.Db.Store.FindPilot(AccountId)!;
        long before = f.Market.Balance(pilot.PilotId);
        Assert.Throws<InvalidDataException>(() =>
            f.Market.Buy(pilot.PilotId, SellingStation, [new(ItemType, UnitPrice, 1), new(int.MaxValue, UnitPrice, 1)])
        );
        Assert.Throws<InvalidDataException>(() =>
            f.Db.Services.GetRequiredService<IPersonalMarket>().PlaceBuy(pilot.PilotId, new(SellingStation, ItemType, UnitPrice, 1, 1))
        );
        Assert.Throws<InvalidDataException>(() => f.Market.Buy(pilot.PilotId, pilot.StationId, [new(ItemType, UnitPrice, 1)]));
        Assert.Equal(before, f.Market.Balance(pilot.PilotId));
        Assert.Empty(f.Market.Transactions(pilot.PilotId));
        using WorldDbContext saved = new(f.Db.Path);
        Assert.Empty(saved.Set<OwnedItem>().Where(i => i.OwnerId == pilot.PilotId && i.TypeId == ItemType));
        Assert.Empty(saved.Set<SavedPersonalOrder>());
        Assert.Empty(saved.Set<NpcMarketOrder>());
    }
}
