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
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId));
        Assert.Equal(3651, item.TypeId);
        Assert.Equal(5, item.Volume);
        var moved = Assert.Single(inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, null));
        Assert.Equal(item, moved.Previous);
        Assert.Equal(item with { LocationId = pilot.ShipId, FlagId = 5 }, moved.Item);
        _ = new SqliteWorld(db.Path);
        Assert.Empty(inventory.List(pilot.PilotId, pilot.StationId));
        Assert.Equal(moved.Item, Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId)));
        Assert.Single(inventory.List(90000002, pilot.StationId));
        inventory.Move(pilot.PilotId, [item.Id], pilot.ShipId, pilot.StationId, 4, null);
        Assert.Equal(item, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId)));
    }

    [Fact]
    public void RejectsForeignItemsBadQuantitiesRemoteHangarsAndSpaceTransfers()
    {
        using TestWorld db = new();
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var pilot = db.Store.FindPilot(1000001)!;
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId));
        var foreign = Assert.Single(inventory.List(90000002, pilot.StationId));
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
        Assert.Equal(item, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId)));
    }

    [Fact]
    public void CapacityValidationIsAtomicAndPartialMovesConserveQuantity()
    {
        using TestWorld db = new();
        var inventory = db.Services.GetRequiredService<IPilotInventory>();
        var pilot = db.Store.FindPilot(1000001)!;
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSql($"UPDATE WorldItem SET Quantity=30 WHERE OwnerId={pilot.PilotId}");
        }
        var item = Assert.Single(inventory.List(pilot.PilotId, pilot.StationId));
        Assert.Throws<InvalidDataException>(() => inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, null));
        Assert.Equal(item, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId)));
        var changes = inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, 25);
        Assert.Equal(2, changes.Count);
        var cargo = Assert.Single(inventory.List(pilot.PilotId, pilot.ShipId));
        Assert.Equal(25, cargo.Quantity);
        Assert.NotEqual(item.Id, cargo.Id);
        Assert.Equal(5, Assert.Single(inventory.List(pilot.PilotId, pilot.StationId)).Quantity);
        Assert.Throws<InvalidDataException>(() => inventory.Move(pilot.PilotId, [item.Id], pilot.StationId, pilot.ShipId, 5, 1));
        Assert.Equal(30, inventory.List(pilot.PilotId, pilot.StationId).Concat(inventory.List(pilot.PilotId, pilot.ShipId)).Sum(i => i.Quantity));
    }

    [Fact]
    public void VersionTwelveMigrationGrantsOnceAndKeepsShipsAndTraining()
    {
        using TestWorld db = new();
        var pilot = db.Store.FindPilot(1000001);
        using (WorldDbContext world = new(db.Path))
        {
            world.Database.ExecuteSqlRaw("DROP TABLE WorldItem; PRAGMA user_version=12");
        }
        _ = new SqliteWorld(db.Path);
        _ = new SqliteWorld(db.Path);
        Assert.Equal(pilot, db.Store.FindPilot(1000001));
        using WorldDbContext saved = new(db.Path);
        Assert.Equal(2, saved.Set<OwnedItem>().Count());
        Assert.Equal(2, saved.Set<PilotSkill>().Count());
    }
}
