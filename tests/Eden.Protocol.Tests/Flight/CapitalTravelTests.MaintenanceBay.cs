using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task VentureStoresAndBoardsTheSameHullAtSharedRorqualAfterRestart()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var guest = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(guest.ShipId, origin + new SpacePosition(3000, 0, 0), Jan);
        long ore = fixture.Grant(guest.PilotId, guest.ShipId, InventoryFlag.Cargo, Cargo, 1);
        using Client host = fixture.Select(1000001);
        using Client miner = fixture.Select(1000002);
        fixture.Join(miner);
        Assert.IsType<PilotCallResult.Failure>(Store());
        Assert.Same(
            PilotCallResult.None,
            host.Call("ship", "ConfigureShip", new Dictionary<string, object?> { [ShipSharingConfigurationWire.MaintenanceFleet] = true })
        );
        Assert.IsType<PilotCallResult.Failure>(Store());
        Assert.Equal(guest.ShipId, miner.Pilot.ShipId);
        using (var action = fixture.Live.Economy.Begin(new(pilots: [guest.PilotId], ships: [guest.ShipId])))
        {
            action.Remove(action.Set<OwnedItem>().Find(ore)!);
            action.Commit();
        }
        var stored = Store();
        Assert.True(stored is PilotCallResult.Success, stored.ToString());
        long capsule = Assert.IsType<long>(Assert.IsType<PilotCallResult.Success>(stored).Reply.ToBlue());
        Assert.Equal(capsule, miner.Pilot.ShipId);
        Assert.Equal(Jan, miner.Pilot.SystemId);
        Assert.NotEqual(guest.ShipId, capsule);
        Assert.Equal((owner.ShipId, guest.PilotId, false), StoredHull());
        var reference = Assert.IsType<BlueObjectReference>(
            Assert.IsType<PilotCallResult.Success>(miner.Call("invbroker", "GetInventoryFromId", owner.ShipId, 0L)).Reply.ToBlue()
        );
        string bound = miner.Context.ResolveReference(reference.Id)!;
        var bay = Assert.IsType<BlueList<InventoryRow>>(
            Assert.IsType<PilotCallResult.Success>(miner.Call(bound, "List", (long)InventoryFlag.ShipMaintenanceBay)).Reply
        );
        Assert.Equal(guest.ShipId, Assert.Single(bay.Items).Id);
        await host.TickAsync();
        Assert.Contains(host.Packets, packet => ContainsText(packet, "OnItemChange"));
        miner.Dispose();
        host.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal((owner.ShipId, guest.PilotId, false), StoredHull());

        using Client returnedHost = fixture.Select(1000001);
        using Client returnedGuest = fixture.Select(1000002);
        Assert.IsType<PilotCallResult.Failure>(returnedGuest.Call("ship", "BoardStoredShip", owner.ShipId, guest.ShipId));
        fixture.Join(returnedGuest);
        var resumed = returnedGuest.Call("ship", "BoardStoredShip", owner.ShipId, guest.ShipId);
        Assert.True(resumed is PilotCallResult.Success, resumed.ToString());
        Assert.Equal(guest.ShipId, returnedGuest.Pilot.ShipId);
        Assert.Equal(Jan, returnedGuest.Pilot.SystemId);
        Assert.Equal(((long?)null, guest.PilotId, true), StoredHull());
        Assert.Contains(fixture.Inventory.List(guest.PilotId, guest.ShipId), item => item.TypeId == LiquidOzone);

        (long? Bay, long Owner, bool InSpace) StoredHull() =>
            fixture.Live.Economy.Inspect(world =>
            {
                var saved = world.Ships.Find(guest.ShipId)!;
                return (saved.StoredInId, saved.OwnerId, saved.Position is not null);
            });
        PilotCallResult Store() => miner.Call("ship", "StoreVessel", owner.ShipId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SharedMaintenanceBayGuestHullSettlesWithHostLoss(bool survives)
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var guest = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(guest.ShipId, origin + new SpacePosition(3000, 0, 0), Jan);
        using Client host = fixture.Select(1000001);
        using Client miner = fixture.Select(1000002);
        fixture.Join(miner);
        Assert.Same(
            PilotCallResult.None,
            host.Call("ship", "ConfigureShip", new Dictionary<string, object?> { [ShipSharingConfigurationWire.MaintenanceFleet] = true })
        );
        Assert.IsType<PilotCallResult.Success>(miner.Call("ship", "StoreVessel", owner.ShipId));
        host.Dispose();
        miner.Dispose();
        using (var world = fixture.Live.Economy.Begin(new(pilots: [owner.PilotId], ships: [owner.ShipId])))
        using (CatalogDbContext catalog = new(new WorldDatabase(fixture.Db.Path).CatalogPath))
        {
            var pilot = world.Pilots.Find(owner.PilotId)!;
            ShipLossPersistence.Apply(
                world,
                catalog,
                pilot,
                null,
                owner.ShipId,
                Jan,
                pilot.ActiveShip.Position!,
                fixture.Clock.Now,
                new SharedBayLootRoll(survives),
                new CombatParty(90000003, null, 1000035, null, 601, null, 0, 1000)
            );
            world.Commit();
        }
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        fixture.Live.Economy.Inspect(world =>
        {
            Assert.Null(world.Ships.Find(owner.ShipId));
            var loss = world.Set<ShipLoss>().Find(owner.ShipId)!;
            Assert.Contains(guest.ShipId.ToString(), loss.StoredShipsJson);
            var hull = world.Ships.Find(guest.ShipId);
            if (survives)
            {
                Assert.Equal(owner.PilotId, hull!.OwnerId);
                Assert.Equal((int)InventoryFlag.Cargo, hull.StorageFlagId);
                Assert.NotNull(hull.StoredInId);
                Assert.Contains(
                    world.Set<OwnedItem>().Where(item => item.LocationId == guest.ShipId),
                    item => item.TypeId == LiquidOzone && item.OwnerId == owner.PilotId
                );
            }
            else
            {
                Assert.Null(hull);
                Assert.Empty(world.Set<OwnedItem>().Where(item => item.LocationId == guest.ShipId));
            }
            return true;
        });
    }

    private sealed class SharedBayLootRoll(bool survives) : IShipLootRoll
    {
        public bool Survives() => survives;
    }
}
