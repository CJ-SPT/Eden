using Eden.Data.World.Inventory;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Inventory;

public sealed partial class CargoContainerAssemblyTests
{
    private const long Owner = ContractAssetTestData.Pilot;
    private const int StationContainer = 17366;
    private const int SmallStandardContainer = 3297;

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void NativeBoundCallAssemblesOneUnitAndCheckpointsTheResult(int quantity)
    {
        using Fixture f = new();
        long source = f.Add(StationContainer, quantity);
        var result = Assert.IsType<PilotCallResult.Success>(f.Bind(source));
        var bound = Assert.IsType<BoundPilotReply>(result.Reply);
        Assert.Equal("invbroker", f.Context.ResolveReference(bound.Reference.Id));
        Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(bound.Reply)));

        var changes = f.Context.Notifications.OfType<ItemChanged>().ToArray();
        var assembled = Assert.Single(changes, change => change.Item.Quantity == InventoryWireDefinitions.AssembledQuantity);
        Assert.DoesNotContain(InventoryColumn.Quantity, assembled.Previous);
        Assert.NotEmpty(BlueMarshal.Encode(assembled.Arguments));
        if (quantity == 1)
        {
            Assert.Single(changes);
            Assert.Equal(source, assembled.Item.Id);
            Assert.Equal(0, assembled.Previous[InventoryColumn.Singleton]);
        }
        else
        {
            Assert.Equal(2, changes.Length);
            Assert.NotEqual(source, assembled.Item.Id);
            Assert.Equal(0, assembled.Previous[InventoryColumn.Location]);
            var remainder = Assert.Single(changes, change => change.Item.Id == source);
            Assert.Equal(quantity - 1, remainder.Item.Quantity);
            Assert.Equal(quantity, remainder.Previous[InventoryColumn.StackSize]);
        }
        Assert.Same(PilotCallResult.None, f.Call([assembled.Item.Id, null, 0d]));
        Assert.Equal(changes.Length, f.Context.Notifications.Count);
        var expected = f.Items(ContractAssetTestData.Station, StationContainer);
        Assert.Equal(quantity, expected.Sum(item => item.Quantity));
        var container = Assert.Single(expected, item => item.Singleton);
        Assert.Equal(2_000_000, container.Volume);
        Assert.Equal(assembled.Item.Id, container.Id);
        if (quantity > 1)
        {
            Assert.Equal(10_000, Assert.Single(expected, item => !item.Singleton).Volume);
        }

        f.Context.Dispose();
        f.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(f.Db.Path), liveWorld: true);
        Assert.Equal(
            expected,
            reopened
                .GetRequiredService<IPilotInventory>()
                .List(Owner, ContractAssetTestData.Station)
                .Where(i => i.TypeId == StationContainer)
        );
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void DockedCargoAssemblyAccountsForOneUnitsVolumeExpansion(int quantity, bool fits)
    {
        using Fixture f = new();
        long ship = f.Context.Pilot.ShipId;
        long source = f.Add(SmallStandardContainer, quantity, ship, flag: InventoryFlag.Cargo);
        var before = f.Items(ship, SmallStandardContainer);
        Assert.Equal(10, Assert.Single(before).Volume);
        var result = f.Bind(source);
        var after = f.Items(ship, SmallStandardContainer);
        if (fits)
        {
            Assert.IsType<PilotCallResult.Success>(result);
            Assert.Equal(quantity, after.Sum(item => item.Quantity));
            Assert.Equal(100, Assert.Single(after, item => item.Singleton).Volume);
            Assert.Equal(2, f.Context.Notifications.OfType<ItemChanged>().Count());
        }
        else
        {
            var failure = Assert.IsType<PilotCallResult.NamedFailure>(result);
            Assert.Equal("NotEnoughCargoSpace", failure.Name);
            Assert.Equal(90, Assert.IsType<CargoSpaceErrorValues>(failure.Values).Required);
            Assert.Equal(before, after);
            Assert.Empty(f.Context.Notifications);
        }
    }

    [Fact]
    public void AssemblyRejectsForeignRemoteNestedNonContainerAndUndockedItemsWithoutChangingInventory()
    {
        using Fixture f = new();
        long source = f.Add(StationContainer, 3);
        long foreign = f.Add(StationContainer, 3, owner: ContractAssetTestData.Other);
        long remote = f.Add(StationContainer, 3, ContractAssetTestData.RemoteStation);
        long nested = f.Add(StationContainer, 3, source, flag: InventoryFlag.Unlocked);
        long module = f.Add(InventoryDefinitions.CivilianMinerType, 3);
        var before = f.Items(ContractAssetTestData.Station, StationContainer);
        foreach (long id in new[] { foreign, remote, nested, module, source })
        {
            Assert.IsType<PilotCallResult.Failure>(f.Bind(id));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call([source, null, 1d]));
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, SpacePosition.Zero);
        Assert.IsType<PilotCallResult.Failure>(f.Call([source, null, 0d]));
        Assert.Equal(before, f.Items(ContractAssetTestData.Station, StationContainer));
        Assert.Empty(f.Context.Notifications);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IServiceScope _scope;
        private readonly PilotCallRouter _router;
        internal TestWorld Db { get; } = new(liveWorld: true);
        internal PilotCallContext Context { get; }

        internal Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    """
                    INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (448,'Audit Log Secure Container',2);
                    INSERT OR IGNORE INTO ItemGroup(Id,Name,CategoryId) VALUES (340,'Secure Cargo',2),(649,'Freight',2);
                    INSERT INTO ItemType(Id,Name,GroupId,Capacity) VALUES
                        (17366,'Station Container',448,1000000),(3297,'Small Standard Container',12,120),
                        (3465,'Small Secure Container',340,120),(33009,'Medium Freight Container',649,5000);
                    INSERT INTO SourceRecord VALUES ('client/types','17366','{{"volume":2000000}}'),
                        ('client/types','3297','{{"volume":100}}'),('client/types','3465','{{"volume":100}}'),
                        ('client/types','33009','{{"volume":5000}}');
                    """
                );
            }
            _scope = Db.Services.CreateScope();
            _router = _scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, TimeProvider.System, new(), Db.Objects),
                42,
                SignedSettings.Empty
            );
            Context.Select(pilot);
        }

        internal long Add(
            int type,
            int quantity,
            long location = ContractAssetTestData.Station,
            long owner = Owner,
            InventoryFlag flag = InventoryFlag.Hangar
        )
        {
            long id = ContractAssetTestData.Stack(Db, quantity, location, owner, (int)flag);
            using WorldDbContext saved = new(Db.Path);
            saved.Set<OwnedItem>().Find(id)!.TypeId = type;
            saved.SaveChanges();
            return id;
        }

        internal InventoryItem[] Items(long location, int type) =>
            Db.Services.GetRequiredService<IPilotInventory>().List(Owner, location).Where(item => item.TypeId == type).ToArray();

        internal PilotCallResult Bind(long itemId) =>
            Invoke(
                "MachoBindObject",
                [
                    new object?[] { (long)ContractAssetTestData.Station, 15L },
                    new object?[] { "AssembleCargoContainer", new object?[] { itemId, null, 0d } },
                ]
            );

        internal PilotCallResult Call(object?[] args) => Invoke("AssembleCargoContainer", args);

        internal PilotCallResult Service(
            string method,
            object?[] args,
            string service = "invbroker",
            Dictionary<string, object?>? keywords = null,
            PilotCallContext? context = null
        ) => _router.Call(context ?? Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        internal string Open(long id, PilotCallContext? context = null)
        {
            var result = Assert.IsType<PilotCallResult.Success>(Service("GetInventoryFromId", [id, 0L], context: context));
            var reference = Assert.IsType<BlueObjectReference>(result.Reply.ToBlue());
            return (context ?? Context).ResolveReference(reference.Id)!;
        }

        internal long Assembled(int type = StationContainer)
        {
            long id = Add(type, 1);
            Assert.Same(PilotCallResult.None, Call([id, null, 0d]));
            Context.Notifications.Clear();
            return id;
        }

        internal PilotCallContext AnotherContext()
        {
            var pilot = Db.Store.FindPilot(1000001)!;
            var context = new PilotCallContext(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, TimeProvider.System, new(), Db.Objects),
                43,
                SignedSettings.Empty
            );
            context.Select(pilot);
            return context;
        }

        private PilotCallResult Invoke(string method, object?[] args) =>
            _router.Call(Context, "invbroker", method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { });

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
