using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task RorqualSharedFleetHangarRevalidatesAccessAndTransfersOwnershipAcrossCheckpoint()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var guest = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(guest.ShipId, origin + new SpacePosition(3_000, 0, 0), Jan);
        long stack = fixture.Grant(guest.PilotId, guest.ShipId, InventoryFlag.Cargo, Cargo, 10);
        using Client rorqual = fixture.Select(1000001);
        using Client miner = fixture.Select(1000002);
        fixture.Join(miner);
        Assert.False(Open() is PilotCallResult.Success);
        Assert.Same(PilotCallResult.None, Share(true));
        var reference = Assert.IsType<BlueObjectReference>(Assert.IsType<PilotCallResult.Success>(Open()).Reply.ToBlue());
        string bound = miner.Context.ResolveReference(reference.Id)!;
        Assert.Equal($"{InventoryHandler.SharedShipInventoryPrefix}{owner.ShipId}", bound);
        var flags = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<PilotCallResult.Success>(miner.Call("ship", "GetShipConfiguration", owner.ShipId)).Reply.ToBlue()
        );
        Assert.Equal(true, flags[ShipSharingConfigurationWire.FleetHangarFleet]);
        Assert.Same(PilotCallResult.None, Move(bound, stack, guest.ShipId, InventoryFlag.FleetHangar, 4));
        var contents = Assert.IsType<BlueList<InventoryRow>>(
            Assert.IsType<PilotCallResult.Success>(miner.Call(bound, "List", (long)InventoryFlag.FleetHangar)).Reply
        );
        long portion = Assert.Single(contents.Items).Id;
        Assert.Equal(4, Assert.Single(contents.Items).Quantity);
        Assert.Equal(owner.PilotId, Assert.Single(fixture.Inventory.List(owner.PilotId, owner.ShipId), i => i.Id == portion).OwnerId);
        Assert.Equal(6, Assert.Single(fixture.Inventory.List(guest.PilotId, guest.ShipId), i => i.Id == stack).Quantity);
        Assert.IsType<PilotCallResult.Failure>(Move("@ship", fixture.CargoId, owner.ShipId, InventoryFlag.Cargo, 1));
        await rorqual.TickAsync();
        Assert.Contains(rorqual.Packets, packet => ContainsText(packet, "OnItemChange"));
        MoveGuest(origin + new SpacePosition(5000, 0, 0));
        Assert.IsType<PilotCallResult.Failure>(miner.Call(bound, "List", (long)InventoryFlag.FleetHangar));
        Assert.IsType<PilotCallResult.Failure>(Move("@ship", portion, owner.ShipId, InventoryFlag.Cargo, 2));
        MoveGuest(origin + new SpacePosition(3000, 0, 0));
        await miner.TickAsync();

        Assert.Same(PilotCallResult.None, Share(false));
        Assert.IsType<PilotCallResult.Failure>(miner.Call(bound, "List", (long)InventoryFlag.FleetHangar));
        Assert.IsType<PilotCallResult.Failure>(Move("@ship", portion, owner.ShipId, InventoryFlag.Cargo, 2));
        Assert.Same(PilotCallResult.None, Share(true));
        // The industrial-cyno fixture packed this Venture with more ozone than its ordinary cargo capacity.
        Assert.IsType<PilotCallResult.NamedFailure>(Move("@ship", portion, owner.ShipId, InventoryFlag.Cargo, null));
        using (var action = fixture.Live.Economy.Begin(new(pilots: [guest.PilotId], ships: [guest.ShipId])))
        {
            action.Remove(action.Set<OwnedItem>().Single(i => i.LocationId == guest.ShipId && i.TypeId == LiquidOzone));
            action.Commit();
        }
        Assert.Same(PilotCallResult.None, Move("@ship", portion, owner.ShipId, InventoryFlag.Cargo, null));
        Assert.Equal(guest.PilotId, Assert.Single(fixture.Inventory.List(guest.PilotId, guest.ShipId), i => i.Id == portion).OwnerId);
        rorqual.Dispose();
        miner.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(guest.PilotId, Assert.Single(fixture.Inventory.List(guest.PilotId, guest.ShipId), i => i.Id == portion).OwnerId);
        using Client reconnected = fixture.Select(1000001);
        var saved = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<PilotCallResult.Success>(reconnected.Call("ship", "GetShipConfiguration", owner.ShipId)).Reply.ToBlue()
        );
        Assert.Equal(true, saved[ShipSharingConfigurationWire.FleetHangarFleet]);

        PilotCallResult Open() => miner.Call("invbroker", "GetInventoryFromId", owner.ShipId, 0L);
        PilotCallResult Share(bool enabled) =>
            rorqual.Call(
                "ship",
                "ConfigureShip",
                new Dictionary<string, object?> { [ShipSharingConfigurationWire.FleetHangarFleet] = enabled }
            );
        PilotCallResult Move(string service, long item, long source, InventoryFlag flag, int? quantity) =>
            miner.CallWithKeywords(service, "Add", new() { ["flag"] = (long)flag, ["qty"] = (long?)quantity }, item, source);
        void MoveGuest(SpacePosition position)
        {
            fixture.Db.Store.SetPosition(guest.ShipId, position, Jan);
            fixture.Db.Services.GetRequiredService<IPilotCombatPresence>().Displace(Jan, guest.ShipId, position);
            miner.Context.Flight.Checkpoint();
        }
    }
}
