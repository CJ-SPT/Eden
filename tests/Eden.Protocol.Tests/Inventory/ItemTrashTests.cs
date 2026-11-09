using Eden.Data.Catalog;
using Eden.Data.World.Combat;
using Eden.Data.World.Contracts;
using Eden.Data.World.Industry;
using Eden.Data.World.Insurance;
using Eden.Data.World.Inventory;
using Eden.Data.World.Market;
using Eden.Data.World.Thermal;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Insurance;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Assets = Eden.Protocol.Tests.Persistence.ContractAssetTestData;

namespace Eden.Protocol.Tests.Inventory;

public sealed class ItemTrashTests
{
    private static TestWorld Create(bool resident = false)
    {
        TestWorld db = new(liveWorld: resident);
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
        catalog.Add(
            new SourceRecord
            {
                Dataset = "client/typelist",
                Key = "298",
                Data = "{}",
            }
        );
        catalog.SaveChanges();
        return db;
    }

    private static IPilotItemTrash Store(TestWorld db) => db.Services.GetRequiredService<IPilotItemTrash>();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void TrashRemovesCompleteStacksAndPackagedShipsAtLocalAndRemoteStationsAcrossReopen(bool remote, bool inSpace)
    {
        using var db = Create();
        Assets.AddRemoteStation(db);
        int station = remote ? Assets.RemoteStation : Assets.Station;
        long module = Assets.Stack(db, 20, station);
        long hull = Assets.Stack(db, 3, station);
        long retained = Assets.Stack(db);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(hull)!.TypeId = 601;
            if (inSpace)
            {
                long active = world.Pilots.Find(Assets.Pilot)!.ActiveShipId;
                world.Ships.Find(active)!.Position = new() { SystemId = 30000142 };
            }
            world.SaveChanges();
        }
        var changes = Store(db).Trash(Assets.Pilot, station, [module, hull]);
        Assert.Equal(2, changes.Count);
        Assert.All(changes, c => Assert.Equal(0, c.Item.Quantity));
        Assert.Equal(20, changes.Single(c => c.Item.Id == module).Previous!.Quantity);
        Assert.Equal(3, changes.Single(c => c.Item.Id == hull).Previous!.Quantity);
        WorldInitialization.Open(db.Path);
        using var reopened = ServerServices.Build(new(db.Path));
        var items = reopened.GetRequiredService<IPilotAssets>().Items(Assets.Pilot, new(IncludeContainers: true));
        Assert.DoesNotContain(items, i => i.Id == module || i.Id == hull);
        Assert.Contains(items, i => i.Id == retained);
        Assert.Throws<InvalidDataException>(() => Store(db).Trash(Assets.Pilot, station, [module]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrashRemovesInactiveHullFittingsLoadedChargesAndStoredShipsWithoutPayout(bool resident)
    {
        using var db = Create(resident);
        long hull = Assets.Hull(db);
        long child = Assets.Hull(db, hull);
        long module = Assets.Stack(db, 1, hull, flag: (int)InventoryFlag.HighSlot0);
        long charges = Assets.Stack(db, 50, module, flag: (int)InventoryFlag.HighSlot0);
        long cargo = Assets.Stack(db, 20, child, flag: (int)InventoryFlag.Cargo);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(module)!.Singleton = true;
            world.Add(new SavedModuleCondition { ModuleId = module, Damage = 0.5 });
            world.Add(
                new ShipHealthState
                {
                    ShipId = hull,
                    ArmorDamage = 5,
                    StructureDamage = 10,
                }
            );
            world.Add(
                new SavedInsurancePolicy
                {
                    PilotId = Assets.Pilot,
                    ShipId = hull,
                    TypeId = 601,
                }
            );
            world.Add(
                new SavedInsurancePolicy
                {
                    PilotId = Assets.Pilot,
                    ShipId = child,
                    TypeId = 601,
                }
            );
            world.SaveChanges();
        }
        var changes = Store(db).Trash(Assets.Pilot, Assets.Station, [hull]);
        Assert.Equal(new[] { hull, child, module, charges, cargo }.Order(), changes.Select(c => c.Item.Id).Order());
        Assert.Equal(hull, changes[^1].Item.Id);
        using (WorldDbContext world = new(db.Path))
        {
            Assert.False(world.Ships.Any(s => s.Id == hull || s.Id == child));
            Assert.False(world.Set<OwnedItem>().Any(i => i.Id == module || i.Id == charges || i.Id == cargo));
            Assert.Empty(world.Set<SavedModuleCondition>());
            Assert.Null(world.Set<ShipHealthState>().Find(hull));
            Assert.All(world.Set<SavedInsurancePolicy>(), p => Assert.Equal(InsurancePolicyStatus.HullRemoved, p.Status));
            Assert.Empty(world.Set<SavedInsuranceLedger>());
            Assert.Empty(world.Set<PilotWallet>());
            Assert.Equal(0, world.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM pragma_foreign_key_check").Single());
        }
        db.Services.Dispose();
        WorldInitialization.Open(db.Path);
        using WorldDbContext saved = new(db.Path);
        Assert.False(saved.Ships.Any(s => s.Id == hull || s.Id == child));
    }

    [Fact]
    public void TrashFromDockedActiveShipCargoRemovesOnlySelectedContents()
    {
        using var db = Create();
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        long cargo = Assets.Stack(db, location: ship, flag: (int)InventoryFlag.Cargo);
        long retained = Assets.Stack(db, location: ship, flag: (int)InventoryFlag.Cargo);
        Assert.Equal(cargo, Assert.Single(Store(db).Trash(Assets.Pilot, Assets.Station, [cargo])).Item.Id);
        using WorldDbContext world = new(db.Path);
        Assert.NotNull(world.Ships.Find(ship));
        Assert.NotNull(world.Set<OwnedItem>().Find(retained));
    }

    [Fact]
    public void TrashContainerRemovesNestedBlueprintAndInvalidatesBlueprintCache()
    {
        using var db = Create();
        using (CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                """
                INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES (2,'Celestial'),(9,'Blueprint');
                INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES (12,'Cargo Container',2),(9009,'Test blueprint',9);
                INSERT INTO ItemType(Id,Name,GroupId) VALUES (3293,'Small Standard Container',12),(990001,'Test blueprint',9009);
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','3293','{{"volume":100}}');
                INSERT INTO SourceRecord(Dataset,Key,Data) VALUES ('client/types','990001','{{"volume":0.01}}');
                """
            );
        }
        long container = Assets.Stack(db, 1);
        long blueprint = Assets.Stack(db, 1, container, flag: (int)InventoryFlag.Unlocked);
        using (WorldDbContext world = new(db.Path))
        {
            world.Set<OwnedItem>().Find(container)!.TypeId = 3293;
            world.Set<OwnedItem>().Find(container)!.Singleton = true;
            world.Set<OwnedItem>().Find(blueprint)!.TypeId = 990001;
            world.SaveChanges();
        }
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        Assert.Same(
            PilotCallResult.None,
            db.Services.GetRequiredService<ItemTrashHandler>()
                .Call(context, "invbroker", "TrashItems", [new List<object?> { container }, (long)Assets.Station])
        );
        Assert.Equal(2, context.Notifications.OfType<ItemChanged>().Count());
        Assert.Equal(Assets.Pilot, Assert.Single(context.Notifications.OfType<BlueprintsUpdated>()).OwnerId);
        using WorldDbContext saved = new(db.Path);
        Assert.Null(saved.Set<OwnedItem>().Find(container));
        Assert.Null(saved.Set<OwnedItem>().Find(blueprint));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectFittingsAndCargoInSpaceCannotBeTrashed(bool fitted)
    {
        using var db = Create();
        long ship = db.Store.FindPilot(1000001)!.ShipId;
        long item = Assets.Stack(db, location: ship, flag: fitted ? (int)InventoryFlag.HighSlot0 : (int)InventoryFlag.Cargo);
        if (!fitted)
        {
            using WorldDbContext world = new(db.Path);
            world.Ships.Find(ship)!.Position = new() { SystemId = 30000142 };
            world.SaveChanges();
        }
        Assert.Throws<InvalidDataException>(() => Store(db).Trash(Assets.Pilot, Assets.Station, [item]));
        using WorldDbContext saved = new(db.Path);
        Assert.NotNull(saved.Set<OwnedItem>().Find(item));
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("active")]
    [InlineData("space")]
    [InlineData("wrongStation")]
    [InlineData("installed")]
    [InlineData("foreignChild")]
    [InlineData("restrictedChild")]
    [InlineData("fitted")]
    [InlineData("unknown")]
    public void InvalidSelectionPreservesTheWholeBatchAndAllContents(string failure)
    {
        using var db = Create();
        long good = Assets.Stack(db);
        long bad = Assets.Hull(db);
        long child = Assets.Stack(db, location: bad, flag: (int)InventoryFlag.Cargo);
        using (WorldDbContext world = new(db.Path))
        {
            if (failure == "foreign")
            {
                world.Database.ExecuteSql($"UPDATE WorldShip SET OwnerId={Assets.Other} WHERE Id={bad}");
            }
            if (failure == "active")
            {
                world.Pilots.Find(Assets.Pilot)!.ActiveShipId = bad;
            }
            if (failure == "space")
            {
                world.Ships.Find(bad)!.Position = new() { SystemId = 30000142 };
            }
            if (failure == "wrongStation")
            {
                world.Ships.Find(bad)!.StationId = Assets.RemoteStation;
            }
            if (failure == "installed")
            {
                world.Add(
                    new IndustryJob
                    {
                        PilotId = Assets.Pilot,
                        BlueprintId = child,
                        StationId = Assets.Station,
                        Status = IndustryJobStatus.Installed,
                        Runs = 1,
                        OutputQuantity = 1,
                        DurationSeconds = 1,
                        StartTime = 1,
                        EndTime = 2,
                    }
                );
            }
            if (failure == "foreignChild")
            {
                world.Set<OwnedItem>().Find(child)!.OwnerId = Assets.Other;
            }
            if (failure == "restrictedChild")
            {
                world.Set<OwnedItem>().Find(child)!.TypeId = 34;
            }
            if (failure == "fitted")
            {
                bad = child;
                world.Set<OwnedItem>().Find(child)!.FlagId = (int)InventoryFlag.HighSlot0;
            }
            world.SaveChanges();
        }
        if (failure == "restrictedChild")
        {
            using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath, writable: true);
            catalog.Set<SourceRecord>().AsTracking().Single(r => r.Dataset == "client/typelist" && r.Key == "298").Data =
                "{\"includedTypeIDs\":[34]}";
            catalog.SaveChanges();
        }
        long selected = failure == "unknown" ? long.MaxValue : bad;
        Assert.Throws<InvalidDataException>(() => Store(db).Trash(Assets.Pilot, Assets.Station, [good, selected]));
        using WorldDbContext saved = new(db.Path);
        Assert.NotNull(saved.Set<OwnedItem>().Find(good));
        Assert.NotNull(saved.Set<OwnedItem>().Find(child));
        Assert.NotNull(saved.Ships.Find(failure == "fitted" ? saved.Set<OwnedItem>().Find(child)!.LocationId : bad));
    }

    [Fact]
    public void ContractEscrowAndItsHiddenContentsCannotBeTrashed()
    {
        using var db = Create();
        long hull = Assets.Hull(db);
        long child = Assets.Stack(db, location: hull, flag: (int)InventoryFlag.Cargo);
        ContractTests.Fund(db, Assets.Pilot);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        contracts.Create(Assets.Pilot, ContractTests.Draft(hull) with { Items = [new(hull, 1)] });
        Assert.Throws<InvalidDataException>(() => Store(db).Trash(Assets.Pilot, Assets.Station, [hull]));
        Assert.Throws<InvalidDataException>(() => Store(db).Trash(Assets.Pilot, Assets.Station, [child]));
        using WorldDbContext world = new(db.Path);
        Assert.NotNull(world.Ships.IgnoreQueryFilters().SingleOrDefault(s => s.Id == hull));
        Assert.NotNull(world.Set<OwnedItem>().IgnoreQueryFilters().SingleOrDefault(i => i.Id == child));
        Assert.NotEmpty(world.Set<SavedContractEscrow>());
    }

    [Fact]
    public void InProgressCourierPackageRemainsIntactAndDeliverableAfterTrashRejection()
    {
        using var db = Create();
        CourierTestData.Prepare(db);
        long item = Assets.Stack(db);
        var contracts = db.Services.GetRequiredService<IPilotContracts>();
        long contract = contracts.Create(Assets.Pilot, CourierTestData.Draft(item));
        contracts.Accept(CourierTestData.Hauler, contract);
        long package;
        using (WorldDbContext world = new(db.Path))
        {
            package = world.Set<SavedContract>().Find(contract)!.PackageId!.Value;
        }
        Assert.Throws<InvalidDataException>(() => Store(db).Trash(CourierTestData.Hauler, Assets.Station, [package]));
        using WorldDbContext saved = new(db.Path);
        Assert.NotNull(saved.Set<OwnedItem>().Find(package));
        Assert.Equal(ContractStatus.InProgress, saved.Set<SavedContract>().Find(contract)!.Status);
        Assert.NotEmpty(saved.Set<SavedContractEscrow>());
    }

    [Fact]
    public void FailedDatabaseDeletionRollsBackAlreadyDeletedRows()
    {
        using var db = Create();
        long first = Assets.Stack(db);
        long second = Assets.Stack(db, 21);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "CREATE TRIGGER RejectTrash BEFORE DELETE ON WorldItem WHEN OLD.Quantity=21 "
                    + "BEGIN SELECT RAISE(ABORT,'injected trash failure'); END"
            );
        }
        Assert.Throws<DbUpdateException>(() => Store(db).Trash(Assets.Pilot, Assets.Station, [first, second]));
        using WorldDbContext saved = new(db.Path);
        Assert.NotNull(saved.Set<OwnedItem>().Find(first));
        Assert.NotNull(saved.Set<OwnedItem>().Find(second));
    }

    [Fact]
    public void NativeBoundTrashReturnsNoneAndPublishesRemovalRowsAfterCommit()
    {
        using var db = Create();
        long hull = Assets.Hull(db);
        long stack = Assets.Stack(db, 20);
        using var scope = db.Services.CreateScope();
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        var router = scope.ServiceProvider.GetRequiredService<PilotCallRouter>();
        object?[] request = [new List<object?> { stack, hull }, (long)Assets.Station];
        var decoded = Assert.IsType<object?[]>(BlueMarshal.Decode(BlueMarshal.Encode(request)));
        var bound = router.Call(
            context,
            "invbroker",
            "MachoBindObject",
            new object?[] { null, new object?[] { "TrashItems", decoded } },
            _ => { }
        );
        var reply = Assert.IsType<BoundPilotReply>(Assert.IsType<PilotCallResult.Success>(bound).Reply);
        Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(reply.Reply)));
        Assert.Equal(2, context.Notifications.Count);
        foreach (var notification in context.Notifications)
        {
            var change = Assert.IsType<ItemChanged>(notification);
            Assert.Equal((long)InventoryLocation.Graveyard, change.Item.LocationId);
            Assert.Equal((long)Assets.Station, change.Previous[InventoryColumn.Location]);
            Assert.Equal(change.Item.Id == stack ? 20 : -1, change.Item.Quantity);
            Assert.NotEmpty(BlueMarshal.Encode(change.Arguments));
        }
        context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(router.Call(context, "invbroker", "TrashItems", decoded, _ => { }));
        Assert.Empty(context.Notifications);
    }

    [Theory]
    [InlineData("tuple")]
    [InlineData("type")]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("station")]
    [InlineData("extra")]
    [InlineData("keyword")]
    public void InvalidNativeRequestsPreserveInventoryAndEmitNoNotifications(string failure)
    {
        using var db = Create();
        long item = Assets.Stack(db);
        var pilot = db.Store.FindPilot(1000001)!;
        using PilotCallContext context = new(
            1000001,
            pilot.Account,
            [pilot],
            p => new(db.Store, p, TimeProvider.System, new(), db.Objects),
            1,
            SignedSettings.Empty
        );
        context.Select(pilot);
        object?[] args = failure switch
        {
            "tuple" => [new object?[] { item }, (long)Assets.Station],
            "type" => [new List<object?> { "item" }, (long)Assets.Station],
            "empty" => [new List<object?>(), (long)Assets.Station],
            "duplicate" => [new List<object?> { item, item }, (long)Assets.Station],
            "station" => [new List<object?> { item }, long.MaxValue],
            "extra" => [new List<object?> { item }, (long)Assets.Station, true],
            _ => [new List<object?> { item }, (long)Assets.Station],
        };
        var handler = db.Services.GetRequiredService<ItemTrashHandler>();
        Assert.IsType<PilotCallResult.Failure>(
            handler.Call(context, "invbroker", "TrashItems", args, failure == "keyword" ? new() { ["force"] = true } : null)
        );
        Assert.Empty(context.Notifications);
        using WorldDbContext saved = new(db.Path);
        Assert.NotNull(saved.Set<OwnedItem>().Find(item));
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("active")]
    [InlineData("committed")]
    [InlineData("restricted")]
    [InlineData("fitted")]
    public void TypedRulesRejectProtectedState(string failure)
    {
        InventoryItem item = new(1, 601, Assets.Pilot, Assets.Station, (int)InventoryFlag.Hangar, 1, 237, 6, 1);
        if (failure == "owner")
        {
            item = item with { OwnerId = Assets.Other };
        }
        if (failure == "fitted")
        {
            item = item with { FlagId = (int)InventoryFlag.HighSlot0 };
        }
        Assert.Throws<InvalidDataException>(() =>
            ItemTrashRules.Check(new(item, Assets.Pilot, failure == "active", failure == "committed", failure == "restricted", true))
        );
        Assert.Throws<InvalidDataException>(() => ItemTrashRules.CheckSelection([1, 1]));
        Assert.Throws<InvalidDataException>(() => ItemTrashRules.CheckSelection([0]));
        Assert.Throws<InvalidDataException>(() => ItemTrashRules.CheckSelection(Enumerable.Range(1, 1001).Select(i => (long)i).ToArray()));
    }

    [Theory]
    [InlineData(true, false, false, false, false, false, true)]
    [InlineData(true, false, false, true, false, false, false)]
    [InlineData(false, true, false, true, false, false, true)]
    [InlineData(true, true, false, false, true, false, false)]
    [InlineData(false, false, true, true, true, false, true)]
    [InlineData(true, true, true, false, false, true, false)]
    [InlineData(false, false, false, false, false, false, false)]
    public void TypeListRestrictionsPreserveNativeInclusionAndExclusionOrder(
        bool category,
        bool group,
        bool type,
        bool excludedCategory,
        bool excludedGroup,
        bool excludedType,
        bool expected
    )
    {
        InventoryItem item = new(1, 601, Assets.Pilot, Assets.Station, (int)InventoryFlag.Hangar, 1, 237, 6, 1);
        ItemTrashTypeList list = new(
            category ? new HashSet<long> { 6 } : [],
            group ? new HashSet<long> { 237 } : [],
            type ? new HashSet<long> { 601 } : [],
            excludedCategory ? new HashSet<long> { 6 } : [],
            excludedGroup ? new HashSet<long> { 237 } : [],
            excludedType ? new HashSet<long> { 601 } : []
        );
        Assert.Equal(expected, list.Contains(item));
    }
}
