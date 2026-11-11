using Eden.Data.World.Industry;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Industry;
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

namespace Eden.Protocol.Tests.PilotSessions;

public sealed class PersonalAssetServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativePersonalAssetsListLocationsAndExpandRemoteStations(bool live)
    {
        using Fixture f = new(live);
        ContractAssetTestData.AddRemoteStation(f.Db);
        long remote = ContractAssetTestData.Stack(f.Db, 100, ContractAssetTestData.RemoteStation);
        long hull = ContractAssetTestData.Hull(f.Db);
        long carried = ContractAssetTestData.Hull(f.Db, hull);
        long cargo = ContractAssetTestData.Stack(f.Db, location: hull, flag: (int)InventoryFlag.Cargo);
        long foreign = ContractAssetTestData.Stack(f.Db, owner: ContractAssetTestData.Other);
        long installed = ContractAssetTestData.Stack(f.Db);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Add(
                new IndustryJob
                {
                    PilotId = ContractAssetTestData.Pilot,
                    BlueprintId = installed,
                    StationId = ContractAssetTestData.Station,
                    Status = IndustryJobStatus.Installed,
                    Runs = 1,
                    OutputQuantity = 1,
                    DurationSeconds = 1,
                    StartTime = 1,
                    EndTime = 2,
                }
            );
            world.SaveChanges();
        }
        f.SelectAndBind("ListStations", []);
        var reply = Assert.IsType<BlueInstance>(f.Decode("ListStations", []));
        var state = BlueRead.Fields(reply.State);
        Assert.Equal(
            new[] { "stationID", "itemCount", "solarSystemID", "typeID" },
            Assert.IsType<List<object?>>(state["header"]).Select(BlueMarshal.Text)
        );
        var locations = Assert.IsType<List<object?>>(state["lines"]).Select(Assert.IsType<object?[]>).ToArray();
        Assert.Equal(2, locations.Length);
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath))
        {
            foreach (var row in locations)
            {
                var station = catalog.FindStation(Assert.IsType<long>(row[0]))!;
                Assert.Equal((long)station.SolarSystemId, row[2]);
                Assert.Equal((long)station.TypeId, row[3]);
            }
        }
        var roots = f.Rows("List", []);
        Assert.Contains(roots, r => Equals(r[0], hull) && Equals(r[5], -1L));
        Assert.Contains(roots, r => Equals(r[0], f.Context.Pilot.ShipId));
        Assert.DoesNotContain(roots, r => new[] { cargo, carried, foreign, installed }.Contains(Assert.IsType<long>(r[0])));
        Assert.Equal(roots.Count, locations.Sum(r => Assert.IsType<long>(r[1])));
        var stack = Assert.Single(f.Rows("ListStationItems", [(long)ContractAssetTestData.RemoteStation]));
        Assert.Equal(remote, stack[0]);
        Assert.Equal(100L, stack[5]);
        Assert.Equal((long)ContractAssetTestData.RemoteStation, stack[3]);
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EscrowRemovalDisappearsFromListsAndStationDirectoryImmediately(bool live)
    {
        using Fixture f = new(live);
        ContractAssetTestData.AddRemoteStation(f.Db);
        long item = ContractAssetTestData.Stack(f.Db, location: ContractAssetTestData.RemoteStation);
        ContractTests.Fund(f.Db, ContractAssetTestData.Pilot);
        f.SelectAndBind("List", []);
        Assert.Contains(f.Rows("ListIncludingContainers", []), r => Equals(r[0], item));
        f.Db.Services.GetRequiredService<IPilotContracts>()
            .Create(
                ContractAssetTestData.Pilot,
                ContractTests.Draft(item) with
                {
                    StationId = ContractAssetTestData.RemoteStation,
                    Items = [new(item, 20)],
                }
            );
        Assert.Empty(f.Rows("ListStationItems", [(long)ContractAssetTestData.RemoteStation]));
        Assert.DoesNotContain(f.Rows("ListIncludingContainers", []), r => Equals(r[0], item));
        var state = BlueRead.Fields(Assert.IsType<BlueInstance>(f.Decode("ListStations", [])).State);
        Assert.DoesNotContain(
            Assert.IsType<List<object?>>(state["lines"]),
            r => Equals(Assert.IsType<object?[]>(r)[0], (long)ContractAssetTestData.RemoteStation)
        );
        Assert.Throws<InvalidDataException>(() => f.Db.Services.GetRequiredService<IPilotAssets>().Items(long.MaxValue, new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SearchIncludesStationContainersAndExcludesShipContentsAcrossReopen(bool live)
    {
        using Fixture f = new(live);
        AddContainerType(f.Db);
        long container = ContractAssetTestData.Stack(f.Db);
        long content = ContractAssetTestData.Stack(f.Db, 42, container, flag: (int)InventoryFlag.Unlocked);
        long foreign = ContractAssetTestData.Stack(f.Db, location: container, owner: ContractAssetTestData.Other, flag: 0);
        long hull = ContractAssetTestData.Hull(f.Db);
        long cargo = ContractAssetTestData.Stack(f.Db, location: hull, flag: (int)InventoryFlag.Cargo);
        long orphan = ContractAssetTestData.Stack(f.Db, location: long.MaxValue, flag: 0);
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(container)!;
            item.TypeId = ContainerType;
            item.Singleton = true;
            world.SaveChanges();
        }
        f.SelectAndBind("ListIncludingContainers", []);
        var search = f.Rows("ListIncludingContainers", []);
        var row = Assert.Single(search, r => Equals(r[0], content));
        Assert.Equal(container, row[3]);
        Assert.Equal((long)InventoryFlag.Unlocked, row[4]);
        Assert.Equal(42L, row[5]);
        Assert.DoesNotContain(search, r => new[] { foreign, cargo, orphan }.Contains(Assert.IsType<long>(r[0])));
        Assert.DoesNotContain(f.Rows("List", []), r => Equals(r[0], content));
        var before = f.Db.Services.GetRequiredService<IPilotAssets>().Items(ContractAssetTestData.Pilot, new(IncludeContainers: true));
        f.Db.Services.Dispose();
        WorldInitialization.Open(f.Db.Path);
        using var reopened = ServerServices.Build(new(f.Db.Path));
        Assert.Equal(before, reopened.GetRequiredService<IPilotAssets>().Items(ContractAssetTestData.Pilot, new(IncludeContainers: true)));
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpaceReadsAndRepeatedReadsReflectCommittedAssetsWithoutLocationRebinding(bool live)
    {
        using Fixture f = new(live);
        ContractAssetTestData.AddRemoteStation(f.Db);
        long ship = f.Db.Store.FindPilot(1000001)!.ShipId;
        f.Db.Store.SetPosition(ship, SpacePosition.Zero);
        f.SelectAndBind("List", []);
        Assert.DoesNotContain(f.Rows("List", []), r => Equals(r[0], ship));
        Assert.Empty(f.Rows("ListStationItems", [(long)ContractAssetTestData.RemoteStation]));
        long remote = ContractAssetTestData.Stack(f.Db, location: ContractAssetTestData.RemoteStation);
        Assert.Equal(remote, Assert.Single(f.Rows("ListStationItems", [(long)ContractAssetTestData.RemoteStation]))[0]);
        f.Context.ReleaseLocationReferences();
        Assert.Equal(remote, Assert.Single(f.Rows("ListStationItems", [(long)ContractAssetTestData.RemoteStation]))[0]);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(remote)!.OwnerId = ContractAssetTestData.Other;
            world.SaveChanges();
        }
        Assert.Empty(f.Rows("ListStationItems", [(long)ContractAssetTestData.RemoteStation]));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void MalformedRequestsRejectAndSafetyTabHasAnHonestEmptyList()
    {
        using Fixture f = new();
        Assert.Same(PilotCallResult.Unsupported, f.Call("GetItemsInSafetyForCharacter", [], "structureAssetSafety"));
        f.SelectAndBind("List", []);
        var before = f.Db.Services.GetRequiredService<IPilotAssets>().Items(ContractAssetTestData.Pilot, new());
        foreach (
            var (method, args) in new (string, object?[])[]
            {
                ("List", [ContractAssetTestData.Other]),
                ("ListIncludingContainers", [true]),
                ("ListStationItems", []),
                ("ListStationItems", [null]),
                ("ListStationItems", [true]),
                ("ListStationItems", [0L]),
                ("ListStationItems", [long.MaxValue]),
                ("ListStationItems", [ContractAssetTestData.Pilot]),
                ("ListStationItems", [(long)ContractAssetTestData.Station, ContractAssetTestData.Other]),
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call(method, args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("List", [], keywords: new() { ["ownerID"] = ContractAssetTestData.Other }));
        Assert.Empty(Assert.IsType<List<object?>>(f.Decode("GetItemsInSafetyForCharacter", [], "structureAssetSafety")));
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("GetItemsInSafetyForCharacter", [ContractAssetTestData.Other], "structureAssetSafety")
        );
        Assert.Same(PilotCallResult.Unsupported, f.Call("MovePersonalAssetsToSafety", [], "structureAssetSafety"));
        Assert.Same(PilotCallResult.Unsupported, f.Call("GetItemsInSafetyForCorp", [], "structureAssetSafety"));
        Assert.Equal(before, f.Db.Services.GetRequiredService<IPilotAssets>().Items(ContractAssetTestData.Pilot, new()));
        Assert.Empty(f.Context.Notifications);
    }

    private const int ContainerType = 3293;

    private static void AddContainerType(TestWorld db)
    {
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (2,'Celestial');
            INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (12,'Cargo Container',2);
            INSERT INTO ItemType(Id,Name,GroupId) VALUES (3293,'Small Standard Container',12);
            INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','3293','{{"volume":100}}');
            """
        );
    }

    private sealed class Fixture : IDisposable
    {
        public TestWorld Db { get; }
        public IServiceScope Scope { get; }
        public PilotCallContext Context { get; }
        private string Service { get; set; } = "charMgr";

        public Fixture(bool live = false)
        {
            Db = new(liveWorld: live);
            Scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Context = new(
                1000001,
                pilot.Account,
                [pilot],
                p => new(Db.Store, p, TimeProvider.System, new(), Db.Objects),
                42,
                SignedSettings.Empty
            );
        }

        public void SelectAndBind(string method, object?[] args)
        {
            Context.Select(Db.Store.FindPilot(1000001)!);
            var result = Call("MachoBindObject", [new object?[] { ContractAssetTestData.Pilot, 10002L }, new object?[] { method, args }]);
            var bound = Assert.IsType<BoundPilotReply>(Assert.IsType<PilotCallResult.Success>(result).Reply);
            Service = Context.ResolveReference(bound.Reference.Id)!;
        }

        public PilotCallResult Call(string method, object?[] args, string? service = null, Dictionary<string, object?>? keywords = null) =>
            Scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service ?? Service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public object? Decode(string method, object?[] args, string? service = null) =>
            BlueMarshal.Decode(BlueMarshal.Encode(Assert.IsType<PilotCallResult.Success>(Call(method, args, service)).Reply));

        public IReadOnlyList<object?[]> Rows(string method, object?[] args)
        {
            var reply = Assert.IsType<PilotCallResult.Success>(Call(method, args)).Reply;
            Assert.NotEmpty(BlueMarshal.Encode(reply));
            return Assert
                .IsType<List<object?>>(WireAssertions.Materialize(reply))
                .Select(value =>
                {
                    var row = Assert.IsType<BlueConstructor>(value);
                    Assert.Equal("blue.DBRow", row.Name);
                    Assert.Equal(BlueMarshal.Encode(InventoryWire.Descriptor()), BlueMarshal.Encode(row.Arguments[0]));
                    return Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(row.Arguments[1])));
                })
                .ToArray();
        }

        public void Dispose()
        {
            Context.Dispose();
            Scope.Dispose();
            Db.Dispose();
        }
    }
}
