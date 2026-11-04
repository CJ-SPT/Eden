using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Contracts;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server.Catalog;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class ContractAssetServiceTests
{
    [Fact]
    public void SplittingPackagedBlueprintsInvalidatesTheBlueprintCacheOnce()
    {
        using Fixture f = new();
        f.Select();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT INTO ItemCategory(Id,Name) VALUES (9,'Blueprint');
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (167,'Hybrid Charge Blueprint',9);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (1130,'Iron Charge S Blueprint',167);
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','1130','{{"volume":0.01}}');
                """
            );
        }
        long id = ContractAssetTestData.Stack(f.Db);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(id)!.TypeId = 1130;
            world.SaveChanges();
        }
        Assert.IsType<PilotCallResult.Success>(f.Call("SplitStack", [60003760L, id, 1L, false, 4L]));
        Assert.Equal(2, f.Context.Notifications.OfType<ItemChanged>().Count());
        Assert.Equal(ContractAssetTestData.Pilot, Assert.Single(f.Context.Notifications.OfType<BlueprintsUpdated>()).OwnerId);
    }

    [Fact]
    public void NativePickersReturnStandardInventoryRowsAndIntegerKeyedCounts()
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Call("GetItemsInDockableLocation", [60003760L, false]));
        f.Select();
        long hull = ContractAssetTestData.Hull(f.Db);
        long cargo = ContractAssetTestData.Stack(f.Db, 25, hull, flag: (int)InventoryFlag.Cargo);
        var list = Rows(f.Call("GetItemsInDockableLocation", [60003760L, false]));
        var ship = Assert.Single(list, r => (long)r[0]! == hull);
        Assert.Equal(new object?[] { hull, 601, ContractAssetTestData.Pilot, 60003760L, 4, -1, 237, 6, "" }, ship);
        var contents = Assert.Single(Rows(f.Call("GetItemsInContainer", [60003760L, hull, 0L, 4L])));
        Assert.Equal(cargo, contents[0]);
        Assert.Equal(hull, contents[3]);
        Assert.Equal(5, contents[4]);
        Assert.Equal(25, contents[5]);
        var reply = Assert.IsType<PilotCallResult.Success>(
            f.Call("GetNumItemsInContainers", [60003760L, new List<object?> { hull }, false, 4L])
        );
        var counts = Assert.IsType<Dictionary<long, object?>>(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        Assert.Equal(1L, counts[hull]);
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void NativeSplitAcknowledgesAndPublishesOldQuantityAndNewStackEntry()
    {
        using Fixture f = new();
        f.Select();
        long id = ContractAssetTestData.Stack(f.Db);
        var result = Assert.IsType<PilotCallResult.Success>(f.Call("SplitStack", [60003760L, id, 7L, false, 4L]));
        Assert.Equal(true, BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        var notifications = f.Context.Notifications.OfType<ItemChanged>().ToArray();
        Assert.Equal(2, notifications.Length);
        Assert.Equal("OnItemChange", notifications[0].Name);
        Assert.Equal(id, notifications[0].Item.Id);
        Assert.Equal(13, notifications[0].Item.Quantity);
        Assert.Equal(20, notifications[0].Previous[InventoryColumn.Quantity]);
        Assert.Equal(20, notifications[0].Previous[InventoryColumn.StackSize]);
        Assert.Equal(7, notifications[1].Item.Quantity);
        Assert.Equal(0, notifications[1].Previous[InventoryColumn.Location]);
        Assert.Equal(0, notifications[1].Previous[InventoryColumn.StackSize]);
        Assert.All(notifications, n => Assert.NotEmpty(BlueMarshal.Encode(n.Arguments)));
        Assert.Equal(
            20,
            f.Db.Services.GetRequiredService<IPilotContractAssets>()
                .Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station)
                .Where(i => i.Id == id || i.Id == notifications[1].Item.Id)
                .Sum(i => i.Quantity)
        );
    }

    [Fact]
    public void PreparationWorksFromSpaceForRemotePersonalHangars()
    {
        using Fixture f = new();
        ContractAssetTestData.AddRemoteStation(f.Db);
        long id = ContractAssetTestData.Stack(f.Db, location: ContractAssetTestData.RemoteStation);
        f.Db.Store.SetPosition(f.Db.Store.FindPilot(1000001)!.ShipId, Eden.Gameplay.Flight.SpacePosition.Zero);
        f.Select();
        Assert.Single(Rows(f.Call("GetItemsInDockableLocation", [(long)ContractAssetTestData.RemoteStation, false])));
        Assert.IsType<PilotCallResult.Success>(f.Call("SplitStack", [(long)ContractAssetTestData.RemoteStation, id, 1L, 0L, 4L]));
    }

    [Fact]
    public void MalformedForeignAndCorporationCallsRejectWithoutNotificationsOrStateChanges()
    {
        using Fixture f = new();
        f.Select();
        long id = ContractAssetTestData.Stack(f.Db);
        long foreign = ContractAssetTestData.Stack(f.Db, owner: ContractAssetTestData.Other);
        var assets = f.Db.Services.GetRequiredService<IPilotContractAssets>();
        var before = assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station);
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("GetItemsInDockableLocation", [60003760L, true]),
                ("GetItemsInDockableLocation", [60003760L, 2L]),
                ("GetItemsInDockableLocation", [long.MaxValue, false]),
                ("GetItemsInDockableLocation", [60003760L]),
                ("GetItemsInContainer", [60003760L, foreign, false, 4L]),
                ("GetNumItemsInContainers", [60003760L, new object?[] { id }, false, 4L]),
                ("GetNumItemsInContainers", [60003760L, new List<object?> { "item" }, false, 4L]),
                ("GetNumItemsInContainers", [60003760L, new List<object?> { id, foreign }, false, 4L]),
                ("SplitStack", [60003760L, id, long.MaxValue, false, 4L]),
                ("SplitStack", [60003760L, id, 1L, true, 4L]),
                ("SplitStack", [60003760L, id, 1L, false, 5L]),
                ("SplitStack", [60003760L, id, 1L, false, null]),
                ("SplitStack", [60003760L, foreign, 1L, false, 4L]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("SplitStack", [60003760L, id, 1L, false, 4L], new() { ["extra"] = true }));
        Assert.Equal(before, assets.Station(ContractAssetTestData.Pilot, ContractAssetTestData.Station));
        Assert.Empty(f.Context.Notifications);
        var handler = f.Scope.ServiceProvider.GetRequiredService<ContractAssetHandler>();
        Assert.Same(PilotCallResult.Unsupported, handler.Call(f.Context, "other", "SplitStack", []));
        Assert.Same(PilotCallResult.Unsupported, handler.Call(f.Context, "contractProxy", "CreateContract", []));
    }

    private static IReadOnlyList<object?[]> Rows(PilotCallResult result)
    {
        var reply = Assert.IsType<PilotCallResult.Success>(result).Reply;
        Assert.NotEmpty(BlueMarshal.Encode(reply));
        var rows = Assert.IsType<List<object?>>(WireAssertions.Materialize(reply));
        return rows.Select(value =>
            {
                var row = Assert.IsType<BlueConstructor>(value);
                Assert.Equal("blue.DBRow", row.Name);
                Assert.Equal(BlueMarshal.Encode(InventoryWire.Descriptor()), BlueMarshal.Encode(row.Arguments[0]));
                return Assert.IsType<object?[]>(row.Arguments[1]);
            })
            .ToArray();
    }

    private sealed class Fixture : IDisposable
    {
        public TestClock Clock { get; } = new();
        public TestWorld Db { get; }
        public IServiceScope Scope { get; }
        public PilotCallContext Context { get; }

        public Fixture()
        {
            Db = new(clock: Clock);
            Scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, Clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

        public PilotCallResult Call(string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            Scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, "contractProxy", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            Scope.Dispose();
            Db.Dispose();
        }
    }
}
