using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Persistence;

public sealed class InventoryPersistenceTests
{
    [Fact]
    public void TransactionPrimitivesSeeUnsavedOutputsAndPreserveIdentityAndCheckedQuantities()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001)!;
        using WorldDbContext world = new(db.Path);
        using var transaction = world.Database.BeginTransaction();
        using CatalogDbContext catalog = new(new WorldDatabase(db.Path).CatalogPath);
        var first = InventoryPersistence.MergeOutput(
            world,
            catalog,
            pilot.PilotId,
            pilot.ShipId,
            (int)InventoryFlag.Cargo,
            InventoryDefinitions.CivilianMinerType,
            2
        );
        var second = InventoryPersistence.MergeOutput(
            world,
            catalog,
            pilot.PilotId,
            pilot.ShipId,
            (int)InventoryFlag.Cargo,
            InventoryDefinitions.CivilianMinerType,
            3
        );
        Assert.Equal(first.Item.Id, second.Item.Id);
        Assert.Equal(5, second.Item.Quantity);
        Assert.Equal(first.Item.Id + 1, InventoryPersistence.NextId(world));
        Assert.Equal(
            first.Item.Volume * 5,
            InventoryPersistence.UsedVolume(world, catalog, pilot.PilotId, pilot.ShipId, (int)InventoryFlag.Cargo)
        );
        Assert.Null(InventoryPersistence.OwnedStack(world, pilot.PilotId + 1, first.Item.Id));
        Assert.Throws<OverflowException>(() =>
            InventoryPersistence.MergeOutput(
                world,
                catalog,
                pilot.PilotId,
                pilot.ShipId,
                (int)InventoryFlag.Cargo,
                InventoryDefinitions.CivilianMinerType,
                int.MaxValue
            )
        );
        Assert.Equal(5, world.Set<OwnedItem>().Local.Single().Quantity);
    }
}
