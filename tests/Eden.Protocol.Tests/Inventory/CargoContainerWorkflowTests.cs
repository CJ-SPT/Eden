using Eden.Gameplay.Inventory;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Persistence;
using Eden.Server;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Inventory;

public sealed partial class CargoContainerAssemblyTests
{
    [Fact]
    public void ContainersInInactiveShipsOpenAndTransferThroughTheActualShipInventory()
    {
        using Fixture f = new();
        const long spare = 1000000000098;
        using (var world = f.Db.Services.GetRequiredService<Eden.World.Runtime.LiveWorld>().Economy.Begin())
        {
            world.Add(
                new Eden.Data.World.Inventory.Ship
                {
                    Id = spare,
                    OwnerId = Owner,
                    TypeId = f.Context.Pilot.ShipTypeId,
                    StationId = f.Context.Pilot.StationId,
                    Name = "Container carrier",
                }
            );
            world.Commit();
        }
        long container = f.Assembled(SmallStandardContainer);
        Assert.Same(PilotCallResult.None, f.Service("Add", [container, (long)ContractAssetTestData.Station], $"@ship:{spare}"));
        string binding = f.Open(container);
        long cargo = f.Add(InventoryDefinitions.CivilianMinerType, 2, spare, flag: InventoryFlag.Cargo);
        Assert.Same(PilotCallResult.None, f.Service("Add", [cargo, spare], binding, new() { ["flag"] = (long)InventoryFlag.None }));
        Assert.Equal(cargo, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType)).Id);
        using (var world = f.Db.Services.GetRequiredService<Eden.World.Runtime.LiveWorld>().Economy.Begin())
        {
            world.Set<Eden.Data.World.Inventory.OwnedItem>().Find(cargo)!.VolumeOverride = 0;
            world.Commit();
        }
        Assert.IsType<PilotCallResult.Failure>(
            f.Service("Add", [cargo, container], $"@ship:{spare}", new() { ["flag"] = (long)InventoryFlag.FleetHangar })
        );
        Assert.Equal(cargo, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType)).Id);
        Assert.IsType<PilotCallResult.Failure>(
            f.Service("Add", [cargo, container], $"@ship:{spare}", new() { ["flag"] = (long)InventoryFlag.DroneBay })
        );
        Assert.Equal(cargo, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType)).Id);
        Assert.Same(PilotCallResult.None, f.Service("Add", [cargo, container], $"@ship:{spare}"));
        Assert.Equal(cargo, Assert.Single(f.Items(spare, InventoryDefinitions.CivilianMinerType)).Id);
        Assert.Empty(f.Items(container, InventoryDefinitions.CivilianMinerType));
        Assert.Empty(f.Items(f.Context.Pilot.ShipId, InventoryDefinitions.CivilianMinerType));
    }

    [Theory]
    [InlineData(SmallStandardContainer)]
    [InlineData(3465)]
    [InlineData(33009)]
    public void OrdinaryContainerDepositsUseNativeNoneFlagAndCanBeStacked(int type)
    {
        using Fixture f = new();
        long container = f.Assembled(type);
        string service = f.Open(container);
        long first = f.Add(InventoryDefinitions.CivilianMinerType, 2);
        Assert.Same(PilotCallResult.None, f.Service("Add", [first, (long)ContractAssetTestData.Station], service, new() { ["qty"] = 1L }));
        Assert.Same(PilotCallResult.None, f.Service("Add", [first, (long)ContractAssetTestData.Station], service));
        Assert.Equal(2, f.Items(container, InventoryDefinitions.CivilianMinerType).Length);
        Assert.Same(PilotCallResult.None, f.Service("StackAll", [(long)InventoryFlag.None], service));
        var stack = Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType));
        Assert.Equal(2, stack.Quantity);
        Assert.Equal((int)InventoryFlag.None, stack.FlagId);
    }

    [Fact]
    public void ContainerStackAllAndMultiMergePublishChangesAndCheckpointQuantities()
    {
        using Fixture f = new();
        long container = f.Assembled();
        string service = f.Open(container);
        long first = f.Add(InventoryDefinitions.CivilianMinerType, 2, container, flag: InventoryFlag.Unlocked);
        long second = f.Add(InventoryDefinitions.CivilianMinerType, 3, container, flag: InventoryFlag.Unlocked);
        long locked = f.Add(InventoryDefinitions.CivilianMinerType, 4, container, flag: InventoryFlag.Locked);
        Assert.Same(PilotCallResult.None, f.Service("StackAll", [], service));
        Assert.Equal(5, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType), item => item.Id == first).Quantity);
        Assert.DoesNotContain(f.Items(container, InventoryDefinitions.CivilianMinerType), item => item.Id == second);
        Assert.Equal(4, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType), item => item.Id == locked).Quantity);
        Assert.Equal(2, f.Context.Notifications.OfType<ItemChanged>().Count());
        long third = f.Add(InventoryDefinitions.CivilianMinerType, 6, container, flag: InventoryFlag.Unlocked);
        Assert.Same(
            PilotCallResult.None,
            f.Service("MultiMerge", [new List<object?> { new object?[] { third, first, 2L } }, container], service)
        );
        Assert.Equal(7, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType), item => item.Id == first).Quantity);
        var expected = f.Items(container, InventoryDefinitions.CivilianMinerType);
        f.Context.Dispose();
        f.Db.Services.Dispose();
        using var reopened = ServerServices.Build(new(f.Db.Path), liveWorld: true);
        Assert.Equal(expected, reopened.GetRequiredService<IPilotInventory>().List(Owner, container));
    }

    [Fact]
    public void ContainerMergesRejectLockedStacksAndRollBackEarlierOperations()
    {
        using Fixture f = new();
        long container = f.Assembled();
        string service = f.Open(container);
        long target = f.Add(InventoryDefinitions.CivilianMinerType, 2, container, flag: InventoryFlag.Unlocked);
        long source = f.Add(InventoryDefinitions.CivilianMinerType, 3, container, flag: InventoryFlag.Unlocked);
        long other = f.Add(InventoryDefinitions.CivilianMinerType, 4, container, flag: InventoryFlag.Unlocked);
        var before = f.Items(container, InventoryDefinitions.CivilianMinerType);
        Assert.IsType<PilotCallResult.Failure>(
            f.Service(
                "MultiMerge",
                [new List<object?> { new object?[] { source, target, 2L }, new object?[] { other, target, 5L } }, container],
                service
            )
        );
        Assert.Equal(before, f.Items(container, InventoryDefinitions.CivilianMinerType));
        Assert.Empty(f.Context.Notifications);
        Assert.IsType<PilotCallResult.Failure>(f.Service("StackAll", [], service, new() { ["flag"] = "bad" }));
        Assert.Empty(f.Context.Notifications);
        Assert.Same(PilotCallResult.None, f.Service("ALSCLockItems", [new List<object?> { target }], service));
        f.Context.Notifications.Clear();
        Assert.IsType<PilotCallResult.Failure>(
            f.Service("MultiMerge", [new List<object?> { new object?[] { source, target, 1L } }, container], service)
        );
        Assert.Empty(f.Context.Notifications);
        Assert.Same(PilotCallResult.None, f.Service("SetPassword", [1L, null, "secret"], service));
        Assert.Same(PilotCallResult.None, f.Service("ALSCConfigSet", [(long)CargoContainerConfiguration.PasswordToOpen], service));
        using var second = f.AnotherContext();
        string binding = f.Open(container, second);
        Assert.Equal(
            "SCGeneralPasswordRequired",
            Assert.IsType<PilotCallResult.NamedFailure>(f.Service("StackAll", [], binding, context: second)).Name
        );
    }

    [Fact]
    public void FittingFromAContainerSplitsTheStackAndRefreshesShipAttributes()
    {
        using Fixture f = new();
        using (CatalogDbContext catalog = new(new WorldDatabase(f.Db.Path).CatalogPath, writable: true))
        {
            catalog.Database.ExecuteSqlRaw("INSERT INTO DogmaAttribute VALUES (14,'hiSlots'); INSERT INTO TypeAttribute VALUES (601,14,2)");
        }
        long container = f.Assembled();
        long module = f.Add(InventoryDefinitions.CivilianMinerType, 2, container, flag: InventoryFlag.Unlocked);
        Assert.Same(
            PilotCallResult.None,
            f.Service("Add", [module, container], "@ship", new() { ["flag"] = (long)InventoryFlag.HighSlot0 })
        );
        Assert.Equal(1, Assert.Single(f.Items(container, InventoryDefinitions.CivilianMinerType)).Quantity);
        var fitted = Assert.Single(
            f.Items(f.Context.Pilot.ShipId, InventoryDefinitions.CivilianMinerType),
            item => item.FlagId == (int)InventoryFlag.HighSlot0
        );
        Assert.True(fitted.Singleton);
        Assert.Equal(1, fitted.Quantity);
        Assert.Contains(f.Context.Notifications, notification => notification is ModuleAttributesChanged);
        Assert.Contains(f.Context.Notifications.OfType<ItemChanged>(), change => change.Item.Id == fitted.Id);
        f.Context.Notifications.Clear();
        var before = f.Items(container, InventoryDefinitions.CivilianMinerType);
        Assert.IsType<PilotCallResult.Failure>(
            f.Service("Add", [module, container], "@ship", new() { ["flag"] = (long)InventoryFlag.HighSlot0 })
        );
        Assert.Equal(before, f.Items(container, InventoryDefinitions.CivilianMinerType));
        Assert.Empty(f.Context.Notifications);
        string binding = f.Open(container);
        Assert.Same(PilotCallResult.None, f.Service("ALSCLockItems", [new List<object?> { module }], binding));
        Assert.IsType<PilotCallResult.Failure>(
            f.Service("Add", [module, container], "@ship", new() { ["flag"] = (long)InventoryFlag.AutoFit })
        );
    }
}
