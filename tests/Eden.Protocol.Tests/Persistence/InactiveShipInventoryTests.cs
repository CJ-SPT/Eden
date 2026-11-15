using Eden.Data.World.Inventory;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InactiveShipInventoryTests
{
    private const long Owner = 90000001;
    private const long Spare = 1000000000098;
    private const long OtherSpare = Spare + 1;
    private const int SpareType = 606;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeMovesUseTheBoundInactiveShipAndSurviveCheckpointReopening(bool multi)
    {
        using Fixture f = new();
        long item = f.Add(2, Spare);
        Assert.Same(PilotCallResult.None, f.Move("@hangar", item, Spare, InventoryFlag.Hangar, multi));
        Assert.Equal(f.Station, Assert.Single(f.Inventory.List(Owner, f.Station), row => row.Id == item).LocationId);
        Assert.Same(PilotCallResult.None, f.Move($"@ship:{OtherSpare}", item, f.Station, InventoryFlag.Cargo, multi));
        Assert.Empty(f.Inventory.List(Owner, f.Active));
        Assert.Same(PilotCallResult.None, f.Move($"@ship:{Spare}", item, OtherSpare, InventoryFlag.Cargo, multi));
        Assert.Same(PilotCallResult.None, f.Move("@ship", item, Spare, InventoryFlag.Cargo, multi));
        Assert.Equal(4, f.Context.Notifications.OfType<ItemChanged>().Count());
        Assert.Equal(2, Assert.Single(f.Inventory.List(Owner, f.Active)).Quantity);
        f.Context.Dispose();
        f.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(f.Db.Path), liveWorld: true);
        Assert.Equal(item, Assert.Single(reopened.GetRequiredService<IPilotInventory>().List(Owner, f.Active)).Id);
        Assert.Empty(reopened.GetRequiredService<IPilotInventory>().List(Owner, Spare));
    }

    [Fact]
    public void InactiveCapacityAndSpecializedHoldFiltersAreAppliedBeforeChangingAnyItems()
    {
        using Fixture f = new();
        long item = f.Add(3, f.Station, InventoryFlag.Hangar);
        // The active hull has 125 m3; the inactive hull has only 10 m3.
        var error = Assert.IsType<PilotCallResult.NamedFailure>(f.Move($"@ship:{Spare}", item, f.Station, InventoryFlag.Cargo));
        Assert.Equal("NotEnoughCargoSpace", error.Name);
        var values = Assert.IsType<CargoSpaceErrorValues>(error.Values);
        Assert.Equal(10, values.Available);
        Assert.Equal(15, values.Required);
        Assert.Equal(3, Assert.Single(f.Inventory.List(Owner, f.Station), row => row.Id == item).Quantity);
        Assert.Empty(f.Context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(f.Move($"@ship:{Spare}", item, f.Station, InventoryFlag.DroneBay));
        Assert.IsType<PilotCallResult.Failure>(f.Move($"@ship:{Spare}", item, f.Station, InventoryFlag.GasHold));
        Assert.Empty(f.Inventory.List(Owner, Spare));
        Assert.Empty(f.Context.Notifications);
        Assert.Same(PilotCallResult.None, f.Move($"@ship:{Spare}", item, f.Station, InventoryFlag.Cargo, quantity: 2));
        Assert.Equal(2, Assert.Single(f.Inventory.List(Owner, Spare)).Quantity);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("remote")]
    [InlineData("stored")]
    [InlineData("space")]
    public void UnavailableShipsRejectSourcesAndDestinationsWithoutChangingState(string failure)
    {
        using Fixture f = new();
        long item = f.Add(1, Spare);
        long local = f.Add(1, f.Station, InventoryFlag.Hangar);
        using (var world = f.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin())
        {
            var ship = world.Ships.Find(Spare)!;
            switch (failure)
            {
                case "foreign":
                    ship.OwnerId = 90000002;
                    break;
                case "remote":
                    ship.StationId++;
                    break;
                case "stored":
                    ship.StoredInId = f.Active;
                    ship.StorageFlagId = (int)InventoryFlag.ShipMaintenanceBay;
                    break;
            }
            world.Commit();
        }
        if (failure == "space")
        {
            f.Db.Store.SetPosition(f.Active, SpacePosition.Zero);
        }
        Assert.Throws<InvalidDataException>(() => f.Inventory.Move(Owner, f.Station, new([item], Spare, (int)InventoryFlag.Hangar, null)));
        Assert.Throws<InvalidDataException>(() => f.Inventory.Move(Owner, Spare, new([local], f.Station, (int)InventoryFlag.Cargo, null)));
        Assert.Equal(item, Assert.Single(f.Inventory.List(Owner, Spare)).Id);
        Assert.Contains(f.Inventory.List(Owner, f.Station), row => row.Id == local);
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void SplitMergeAndStackAllWorkInInactiveCargoWithoutConsumingCapacity()
    {
        using Fixture f = new();
        long first = f.Add(8, Spare);
        Assert.Same(PilotCallResult.None, f.Call("invbroker", "SplitStack", [f.Station, first, 3L, Owner]));
        var split = Assert.Single(f.Inventory.List(Owner, Spare), item => item.Id != first);
        Assert.Equal(3, split.Quantity);
        Assert.Same(
            PilotCallResult.None,
            f.Call($"@ship:{Spare}", "MultiMerge", [new List<object?> { new object?[] { split.Id, first, 2L } }, Spare])
        );
        Assert.Same(PilotCallResult.None, f.Call($"@ship:{Spare}", "StackAll", []));
        Assert.Equal(8, Assert.Single(f.Inventory.List(Owner, Spare)).Quantity);
        // A normal same-hold drag must remain a no-op even in an overloaded hold.
        f.Context.Notifications.Clear();
        Assert.Same(PilotCallResult.None, f.Move($"@ship:{Spare}", first, Spare, InventoryFlag.Cargo));
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void CrossShipMergeUsesDestinationCapacityAndRollsBackTheEntireBatch()
    {
        using Fixture f = new();
        long source = f.Add(3, f.Active);
        long target = f.Add(1, Spare);
        long second = f.Add(1, f.Active);
        Assert.IsType<PilotCallResult.NamedFailure>(
            f.Call(
                $"@ship:{Spare}",
                "MultiMerge",
                [new List<object?> { new object?[] { source, target, 1L }, new object?[] { second, target, 1L } }, f.Active]
            )
        );
        Assert.Equal(1, Assert.Single(f.Inventory.List(Owner, Spare)).Quantity);
        Assert.Equal(4, f.Inventory.List(Owner, f.Active).Sum(item => item.Quantity));
        Assert.Empty(f.Context.Notifications);
        Assert.Same(
            PilotCallResult.None,
            f.Call($"@ship:{Spare}", "MultiMerge", [new List<object?> { new object?[] { source, target, 1L } }, f.Active])
        );
        Assert.Equal(2, Assert.Single(f.Inventory.List(Owner, Spare)).Quantity);
        Assert.Equal(3, f.Inventory.List(Owner, f.Active).Sum(item => item.Quantity));
    }

    [Fact]
    public void MixedValidAndForeignItemsRejectTheEntireMove()
    {
        using Fixture f = new();
        long first = f.Add(1, Spare);
        long foreign = f.Add(1, Spare, owner: 90000002);
        Assert.IsType<PilotCallResult.Failure>(
            f.Call("@hangar", "MultiAdd", [new List<object?> { first, foreign }, Spare], new() { ["flag"] = (long)InventoryFlag.Hangar })
        );
        Assert.Equal(first, Assert.Single(f.Inventory.List(Owner, Spare)).Id);
        Assert.Equal(foreign, Assert.Single(f.Inventory.List(90000002, Spare)).Id);
        Assert.Empty(f.Context.Notifications);
    }

    [Fact]
    public void UnfittingAnInactiveShipUsesItsIdentityForOnlineNotifications()
    {
        using Fixture f = new();
        long module = f.Add(1, Spare, InventoryFlag.HighSlot0);
        using (var world = f.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin())
        {
            var item = world.Set<OwnedItem>().Find(module)!;
            item.Singleton = true;
            item.Online = true;
            world.Commit();
        }
        Assert.Same(PilotCallResult.None, f.Move("@hangar", module, Spare, InventoryFlag.Hangar));
        var effect = Assert.Single(f.Context.Notifications.OfType<ShipEffectChanged>());
        Assert.Equal(Spare, effect.Effect.ShipId);
        Assert.False(effect.Active);
        Assert.DoesNotContain(f.Context.Notifications, notification => notification is ModuleAttributesChanged);
        Assert.False(Assert.Single(f.Inventory.List(Owner, f.Station), item => item.Id == module).Online);
        Assert.Empty(f.Inventory.List(Owner, Spare));
    }

    [Fact]
    public void InactiveSpecializedHoldsAcceptEligibleCargoAndRejectOverCapacity()
    {
        using Fixture f = new();
        const int droneType = 2488;
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO ItemCategory(Id,Name) VALUES(18,'Drone'); "
                    + "INSERT INTO ItemGroup(Id,Name,CategoryId) VALUES(100,'Combat Drone',18); "
                    + "INSERT INTO ItemType(Id,Name,GroupId) VALUES(2488,'Hobgoblin I',100); "
                    + "INSERT INTO SourceRecord VALUES('client/types','2488','{{\"volume\":5.0}}')"
            );
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO DogmaAttribute VALUES({0},'droneCapacity'); " + "INSERT INTO TypeAttribute VALUES(606,{0},10)",
                (int)DogmaAttributeId.DroneCapacity
            );
        }
        long item = f.Add(3, f.Station, InventoryFlag.Hangar, type: droneType);
        Assert.Same(PilotCallResult.None, f.Move($"@ship:{Spare}", item, f.Station, InventoryFlag.DroneBay, quantity: 2));
        Assert.Equal(2, Assert.Single(f.Inventory.List(Owner, Spare)).Quantity);
        Assert.IsType<PilotCallResult.NamedFailure>(f.Move($"@ship:{Spare}", item, f.Station, InventoryFlag.DroneBay));
        long cargo = Assert.Single(f.Inventory.List(Owner, Spare)).Id;
        Assert.Same(PilotCallResult.None, f.Move("@hangar", cargo, Spare, InventoryFlag.Hangar));
        Assert.Empty(f.Inventory.List(Owner, Spare));
    }

    [Fact]
    public void AShipCannotBeMovedInsideItself()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw(
                "INSERT OR IGNORE INTO DogmaAttribute VALUES({0},'shipMaintenanceBayCapacity'); "
                    + "INSERT INTO TypeAttribute VALUES(606,{0},1000000)",
                (int)DogmaAttributeId.ShipMaintenanceBayCapacity
            );
        }
        Assert.IsType<PilotCallResult.Failure>(f.Move($"@ship:{Spare}", Spare, f.Station, InventoryFlag.ShipMaintenanceBay));
        using var world = f.Db.Services.GetRequiredService<LiveWorld>().Economy.Begin();
        Assert.Null(world.Ships.Find(Spare)!.StoredInId);
    }

    [Fact]
    public void ShipAccessRulesAllowOnlyOwnedActiveOrDirectLocalHangarShips()
    {
        ShipInventoryAccess access = new(Owner, 1, 2, true);
        ShipStorageLocation ship = new(3, Owner, 2, (int)InventoryFlag.Hangar);
        Assert.True(InventoryMoveRules.CanUseShip(access, ship));
        Assert.True(InventoryMoveRules.CanUseShip(access with { Docked = false }, ship with { Id = 1 }));
        Assert.False(InventoryMoveRules.CanUseShip(access with { Docked = false }, ship));
        Assert.False(InventoryMoveRules.CanUseShip(access, ship with { OwnerId = Owner + 1 }));
        Assert.False(InventoryMoveRules.CanUseShip(access, ship with { LocationId = 4 }));
        Assert.False(InventoryMoveRules.CanUseShip(access, ship with { FlagId = (int)InventoryFlag.ShipMaintenanceBay }));
    }

    private sealed class Fixture : IDisposable
    {
        internal TestWorld Db { get; } = new(liveWorld: true);
        internal IPilotInventory Inventory => Db.Services.GetRequiredService<IPilotInventory>();
        internal PilotCallContext Context { get; }
        internal long Station { get; }
        internal long Active { get; }
        private readonly IServiceScope _scope;

        internal Fixture()
        {
            using (CatalogDbContext catalog = new(new WorldDatabase(Db.Path).CatalogPath, writable: true))
            {
                catalog.Database.ExecuteSqlRaw(
                    "INSERT INTO ItemType(Id,Name,GroupId,Capacity) VALUES(606,'Spare hull',237,10); "
                        + "INSERT INTO SourceRecord VALUES('client/types','606','{{\"volume\":15000.0}}'); "
                        + "INSERT INTO TypeAttribute SELECT 606,AttributeId,Value FROM TypeAttribute WHERE ItemTypeId=601"
                );
            }
            var pilot = Db.Store.FindPilot(1000001)!;
            Station = pilot.StationId;
            Active = pilot.ShipId;
            using (var world = Db.Services.GetRequiredService<LiveWorld>().Economy.Begin())
            {
                foreach (long id in new[] { Spare, OtherSpare })
                {
                    world.Add(
                        new Ship
                        {
                            Id = id,
                            OwnerId = Owner,
                            TypeId = SpareType,
                            StationId = pilot.StationId,
                            Name = "Spare",
                        }
                    );
                }
                world.Commit();
            }
            _scope = Db.Services.CreateScope();
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
            int quantity,
            long location,
            InventoryFlag flag = InventoryFlag.Cargo,
            long owner = Owner,
            int type = InventoryDefinitions.CivilianMinerType
        )
        {
            using var world = Db.Services.GetRequiredService<LiveWorld>().Economy.Begin();
            long id = world.NextItemId();
            world.Add(
                new OwnedItem
                {
                    Id = id,
                    OwnerId = owner,
                    TypeId = type,
                    LocationId = location,
                    FlagId = (int)flag,
                    Quantity = quantity,
                }
            );
            world.Commit();
            return id;
        }

        internal PilotCallResult Move(string service, long item, long source, InventoryFlag flag, bool multi = false, int? quantity = null)
        {
            Dictionary<string, object?> keywords = new() { ["flag"] = (long)flag };
            if (quantity is int value)
            {
                keywords["qty"] = (long)value;
            }
            return Call(service, multi ? "MultiAdd" : "Add", [multi ? new List<object?> { item } : item, source], keywords);
        }

        internal PilotCallResult Call(string service, string method, object?[] args, Dictionary<string, object?>? keywords = null) =>
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
