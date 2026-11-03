using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Persistence;

public sealed class PilotInventoryTests
{
    [Fact]
    public void StarterGrantAndCargoMoveSurviveReopeningWithoutDuplication()
    {
        using TestWorld db = new();
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var pilot = db.Store.FindPilot(1000001)!;
        Assert.Equal(
            new[] { (int)CombatWeaponType.CivilianGatlingRailgun, InventoryDefinitions.CivilianMinerType },
            inventory.List(pilot.PilotId, pilot.StationId).Select(i => i.TypeId).Order()
        );
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        Assert.Equal(3651, item.TypeId);
        Assert.Equal(5, item.Volume);
        var moved = Assert.Single(inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, null));
        Assert.Equal(item, moved.Previous);
        Assert.Equal(item with { LocationId = pilot.ShipId, FlagId = 5 }, moved.Item);
        _ = new SqliteWorld(db.Path);
        Assert.Equal((int)CombatWeaponType.CivilianGatlingRailgun, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId)).TypeId);
        Assert.Equal(moved.Item, Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)));
        Assert.Single(inventory.List(90000002, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        inventory.Move(pilot.PilotId, [item.Id], pilot.ShipId, pilot.StationId, 4, null);
        Assert.Equal(
            item,
            Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType)
        );
    }

    [Fact]
    public void RejectsForeignItemsBadQuantitiesRemoteHangarsAndSpaceTransfers()
    {
        using TestWorld db = new();
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var pilot = db.Store.FindPilot(1000001)!;
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        var foreign = Assert.Single(inventory.List(90000002, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        void Reject(long[] ids, long source, long destination, int flag, int? qty = null) =>
            Assert.Throws<InvalidDataException>(() => inventory.Move(pilot.PilotId, ids, source, destination, flag, qty));
        Reject([foreign.Id], pilot.StationId, pilot.ShipId, 5);
        Reject([item.Id, foreign.Id], pilot.StationId, pilot.ShipId, 5);
        Reject([item.Id, item.Id], pilot.StationId, pilot.ShipId, 5);
        Reject([item.Id], pilot.StationId, pilot.ShipId, 5, 0);
        Reject([item.Id], pilot.StationId, pilot.ShipId, 5, 2);
        Reject([item.Id], pilot.StationId, pilot.ShipId, 27);
        Reject([item.Id], pilot.StationId + 1, pilot.ShipId, 5);
        db.Store.SetPosition(pilot.ShipId, SpacePosition.Zero);
        Reject([item.Id], pilot.StationId, pilot.ShipId, 5);
        Assert.Equal(
            item,
            Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType)
        );
    }

    [Fact]
    public void CapacityValidationIsAtomicAndPartialMovesConserveQuantity()
    {
        using TestWorld db = new();
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldItem SET Quantity=30 WHERE OwnerId={pilot.PilotId} AND TypeId=3651");
        }
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType);
        Assert.Throws<CargoCapacityException>(() => inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, null));
        Assert.Equal(
            item,
            Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType)
        );
        var changes = inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, 25);
        Assert.Equal(2, changes.Count);
        var cargo = Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(25, cargo.Quantity);
        Assert.NotEqual(item.Id, cargo.Id);
        Assert.Equal(
            5,
            Assert.Single(inventory.List(pilot.PilotId, pilot.StationId), i => i.TypeId == InventoryDefinitions.CivilianMinerType).Quantity
        );
        Assert.Throws<CargoCapacityException>(() => inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, 1));
        Assert.Equal(
            30,
            inventory
                .List(pilot.PilotId, pilot.StationId)
                .Concat(inventory.List(pilot.PilotId, pilot.ShipId))
                .Where(i => i.TypeId == InventoryDefinitions.CivilianMinerType)
                .Sum(i => i.Quantity)
        );
    }

    [Fact]
    public void VersionTwelveMigrationGrantsOnceAndKeepsShipsAndTraining()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw(
                "DROP TABLE WorldItem; DROP TABLE WorldAsteroid; DROP TABLE WorldShipEnergy; DROP TABLE "
                    + "WorldMarketTrade; DROP TABLE WorldMarketOrder; DROP TABLE WorldMarketStation; DROP TABLE "
                    + "WorldWallet; DROP TABLE WorldShipHealth; DROP TABLE WorldWreck; DROP TABLE WorldRepairPayment; DROP "
                    + "TABLE WorldNpcAggression; DROP TABLE WorldShipLoss; DROP TABLE WorldNpc; DROP TABLE WorldNpcBounty; "
                    + "DROP TABLE WorldWeaponCooldown; PRAGMA user_version=12"
            );
        }
        _ = new SqliteWorld(db.Path);
        _ = new SqliteWorld(db.Path);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(4, saved.Set<OwnedItem>().Count());
        Assert.Equal(2, saved.Set<PilotSkill>().Count());
    }
}
