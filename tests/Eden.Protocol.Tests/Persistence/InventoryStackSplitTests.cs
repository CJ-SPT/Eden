using Eden.Data.Industry;
using Eden.Data.Inventory;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InventoryStackSplitTests
{
    [Theory]
    [InlineData(false, InventoryFlag.Hangar)]
    [InlineData(true, InventoryFlag.Hangar)]
    [InlineData(false, InventoryFlag.Cargo)]
    [InlineData(true, InventoryFlag.Cargo)]
    public void NativeDivideCreates49And1InjectorsInPlaceWithNotificationsAndPersistentMetadata(bool menu, InventoryFlag flag)
    {
        using Fixture f = new(flag);
        long existing = SkillInjectorTestData.Item(f.Db, quantity: 10);
        using (WorldDbContext world = new(f.Db.Path))
        {
            foreach (long id in new[] { f.ItemId, existing })
            {
                var item = world.Set<OwnedItem>().Find(id)!;
                item.LocationId = f.Location;
                item.FlagId = (int)flag;
                item.VolumeOverride = 3.25;
                item.CrystalDamage = 0.125;
            }
            world.SaveChanges();
        }
        Assert.Same(PilotCallResult.Unsupported, f.Divide(menu, 1));
        f.Select();
        // Cargo is deliberately over its fitting capacity already. Splitting adds no volume.
        var result = Assert.IsType<PilotCallResult.Success>(f.Divide(menu, 1));
        Assert.Null(BlueMarshal.Decode(BlueMarshal.Encode(result.Reply)));
        var changes = f.Context.Notifications.OfType<ItemChanged>().ToArray();
        Assert.Equal(2, changes.Length);
        Assert.Equal(f.ItemId, changes[0].Item.Id);
        Assert.Equal(49, changes[0].Item.Quantity);
        Assert.Equal(50, changes[0].Previous[InventoryColumn.Quantity]);
        Assert.Equal(50, changes[0].Previous[InventoryColumn.StackSize]);
        Assert.Equal(1, changes[1].Item.Quantity);
        Assert.NotEqual(f.ItemId, changes[1].Item.Id);
        Assert.NotEqual(existing, changes[1].Item.Id);
        Assert.Equal(0, changes[1].Previous[InventoryColumn.Location]);
        Assert.Equal(0, changes[1].Previous[InventoryColumn.StackSize]);
        Assert.All(
            changes,
            change =>
            {
                Assert.Equal(f.Location, change.Item.LocationId);
                Assert.Equal((int)flag, change.Item.FlagId);
                Assert.Equal(f.Context.Pilot.PilotId, change.Item.OwnerId);
                Assert.NotEmpty(BlueMarshal.Encode(change.Arguments));
            }
        );
        _ = WorldInitialization.Open(f.Db.Path);
        using WorldDbContext saved = new(f.Db.Path);
        var rows = saved
            .Set<OwnedItem>()
            .Where(item => item.Id == f.ItemId || item.Id == existing || item.Id == changes[1].Item.Id)
            .ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal(60, rows.Sum(item => item.Quantity));
        Assert.Equal(10, rows.Single(item => item.Id == existing).Quantity);
        Assert.All(
            rows,
            item =>
            {
                Assert.Equal(f.Location, item.LocationId);
                Assert.Equal((int)flag, item.FlagId);
                Assert.Equal(3.25, item.VolumeOverride);
                Assert.Equal(0.125, item.CrystalDamage);
            }
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChoosingTheEntireStackAcknowledgesWithoutCreatingOrChangingRows(bool menu)
    {
        using Fixture f = new(InventoryFlag.Hangar);
        f.Select();
        var before = f.Inventory.List(SkillInjectorTestData.Pilot, f.Location);
        Assert.IsType<PilotCallResult.Success>(f.Divide(menu, 50));
        Assert.Equal(before, f.Inventory.List(SkillInjectorTestData.Pilot, f.Location));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void ShiftDivideInSpaceStaysInTheActiveCargoHoldAndCannotMoveBetweenHoldsOrToTheHangar()
    {
        using Fixture f = new(InventoryFlag.Cargo);
        f.Db.Store.SetPosition(f.Db.Store.FindPilot(1000001)!.ShipId, Eden.Gameplay.Flight.SpacePosition.Zero);
        f.Select();
        Assert.IsType<PilotCallResult.Success>(f.Divide(false, 1));
        var before = f.Inventory.List(SkillInjectorTestData.Pilot, f.Location);
        Assert.Equal(new[] { 1, 49 }, before.Where(item => item.TypeId == 40520).Select(item => item.Quantity).Order());
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("@ship", "Add", [f.ItemId, f.Location], new() { ["qty"] = 1L, ["flag"] = (long)InventoryFlag.DroneBay })
        );
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("@hangar", "Add", [f.ItemId, f.Location], new() { ["qty"] = 1L, ["flag"] = (long)InventoryFlag.Hangar })
        );
        // The separate menu command follows the pinned client's station-only rule.
        Assert.IsType<PilotCallResult.Failure>(f.Divide(true, 1));
        Assert.Equal(before, f.Inventory.List(SkillInjectorTestData.Pilot, f.Location));
        Assert.Equal(2, f.Context.Notifications.OfType<ItemChanged>().Count());
    }

    [Fact]
    public void MalformedForeignRemoteAndSpaceSplitsRejectWithUnchangedStacksAndNoNotifications()
    {
        using Fixture f = new(InventoryFlag.Hangar);
        f.Select();
        long foreign = SkillInjectorTestData.Item(f.Db, quantity: 50);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Set<OwnedItem>().Find(foreign)!.OwnerId = ContractAssetTestData.Other;
            world.SaveChanges();
        }
        var before = f.Inventory.List(SkillInjectorTestData.Pilot, f.Location);
        foreach (long quantity in new[] { 0L, -1, 51, long.MaxValue })
        {
            Assert.IsType<PilotCallResult.Failure>(f.Divide(false, quantity));
            Assert.IsType<PilotCallResult.Failure>(f.Divide(true, quantity));
        }
        foreach (
            var args in new object?[][]
            {
                [],
                [f.Location, f.ItemId, 1L],
                [f.Location, f.ItemId, 1d, SkillInjectorTestData.Pilot],
                [f.Location, f.ItemId, 1L, false],
                [f.Location, f.ItemId, 1L, ContractAssetTestData.Other],
                [f.Location + 1, f.ItemId, 1L, SkillInjectorTestData.Pilot],
                [f.Location, foreign, 1L, SkillInjectorTestData.Pilot],
            }
        )
        {
            Assert.IsType<PilotCallResult.Failure>(f.Call("invbroker", "SplitStack", args));
        }
        Assert.IsType<PilotCallResult.Failure>(f.Call("@hangar", "Add", [foreign, f.Location], new() { ["qty"] = 1L, ["flag"] = 4L }));
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("invbroker", "SplitStack", [f.Location, f.ItemId, 1L, SkillInjectorTestData.Pilot], new() { ["flag"] = 4L })
        );
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("@hangar", "MultiAdd", [new List<object?> { f.ItemId, foreign }, f.Location], new() { ["qty"] = 1L })
        );
        f.Db.Store.SetPosition(f.Context.Pilot.ShipId, Eden.Gameplay.Flight.SpacePosition.Zero);
        f.Context.ChangeShip(f.Db.Store.FindPilot(1000001)!);
        Assert.IsType<PilotCallResult.Failure>(f.Divide(true, 1));
        Assert.IsType<PilotCallResult.Failure>(f.Divide(false, 1));
        Assert.Equal(before, f.Inventory.List(SkillInjectorTestData.Pilot, f.Location));
        Assert.Equal(50, Assert.Single(f.Inventory.List(ContractAssetTestData.Other, f.Location), item => item.Id == foreign).Quantity);
        Assert.Empty(f.Context.Notifications);
    }

    [Theory]
    [InlineData("singleton")]
    [InlineData("online")]
    [InlineData("contents")]
    [InlineData("installed")]
    public void IneligibleStacksCannotBeDuplicatedByASameContainerAdd(string state)
    {
        using Fixture f = new(InventoryFlag.Hangar);
        f.Select();
        using (WorldDbContext world = new(f.Db.Path))
        {
            var item = world.Set<OwnedItem>().Find(f.ItemId)!;
            item.Singleton = state == "singleton";
            item.Online = state == "online";
            if (state == "contents")
            {
                world.Add(
                    new OwnedItem
                    {
                        Id = InventoryPersistence.NextId(world),
                        OwnerId = item.OwnerId,
                        TypeId = item.TypeId,
                        LocationId = item.Id,
                        FlagId = (int)InventoryFlag.Cargo,
                        Quantity = 1,
                    }
                );
            }
            if (state == "installed")
            {
                world.Add(
                    new IndustryJob
                    {
                        PilotId = item.OwnerId,
                        BlueprintId = item.Id,
                        StationId = checked((int)item.LocationId),
                        Status = IndustryJobStatus.Installed,
                        Runs = 1,
                        OutputQuantity = 1,
                        DurationSeconds = 1,
                        StartTime = 1,
                        EndTime = 2,
                    }
                );
            }
            world.SaveChanges();
        }
        var before = f.Inventory.List(SkillInjectorTestData.Pilot, f.Location);
        Assert.IsType<PilotCallResult.Failure>(f.Divide(false, 1));
        Assert.IsType<PilotCallResult.Failure>(f.Divide(true, 1));
        Assert.Equal(before, f.Inventory.List(SkillInjectorTestData.Pilot, f.Location));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void FailedNewStackInsertRollsBackTheOriginalQuantity()
    {
        using Fixture f = new(InventoryFlag.Hangar);
        using (WorldDbContext world = new(f.Db.Path))
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TRIGGER FailInventorySplit BEFORE INSERT ON WorldItem
                WHEN NEW.TypeId=40520 AND NEW.Quantity=1
                BEGIN SELECT RAISE(ABORT,'test split failure'); END;
                """
            );
        }
        var before = f.Inventory.List(SkillInjectorTestData.Pilot, f.Location);
        Assert.Throws<DbUpdateException>(() =>
            f.Inventory.Move(SkillInjectorTestData.Pilot, f.Location, new([f.ItemId], f.Location, (int)InventoryFlag.Hangar, 1))
        );
        Assert.Equal(before, f.Inventory.List(SkillInjectorTestData.Pilot, f.Location));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly IServiceScope _scope;
        public TestWorld Db { get; }
        public PilotCallContext Context { get; }
        public long ItemId { get; }
        public long Location { get; }
        private InventoryFlag Flag { get; }
        private int StationId { get; }
        public IPilotInventory Inventory => _scope.ServiceProvider.GetRequiredService<IPilotInventory>();

        public Fixture(InventoryFlag flag)
        {
            var clock = SkillInjectorTestData.Clock();
            Db = SkillInjectorTestData.Create(clock);
            _scope = Db.Services.CreateScope();
            var pilot = Db.Store.FindPilot(1000001)!;
            Flag = flag;
            StationId = pilot.StationId;
            Location = flag == InventoryFlag.Hangar ? pilot.StationId : pilot.ShipId;
            ItemId = SkillInjectorTestData.Item(Db, quantity: 50);
            using (WorldDbContext world = new(Db.Path))
            {
                var item = world.Set<OwnedItem>().Find(ItemId)!;
                item.LocationId = Location;
                item.FlagId = (int)flag;
                world.SaveChanges();
            }
            Context = new(1000001, pilot.Account, [pilot], p => new(Db.Store, p, clock, new(), Db.Objects), 42, SignedSettings.Empty);
        }

        public void Select() => Context.Select(Db.Store.FindPilot(1000001)!);

        public PilotCallResult Divide(bool menu, long quantity) =>
            menu
                ? Call("invbroker", "SplitStack", [(long)StationId, ItemId, quantity, SkillInjectorTestData.Pilot])
                : Call(
                    Flag == InventoryFlag.Hangar ? "@hangar" : "@ship",
                    "Add",
                    [ItemId, Location],
                    new() { ["qty"] = quantity, ["flag"] = (long)Flag }
                );

        public PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
            _scope
                .ServiceProvider.GetRequiredService<PilotCallRouter>()
                .Call(Context, service, method, BlueMarshal.Decode(BlueMarshal.Encode(args)), _ => { }, keywords);

        public void Dispose()
        {
            Context.Dispose();
            _scope.Dispose();
            Db.Dispose();
        }
    }
}
