using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Ammunition;
using Client = Eden.Protocol.Tests.PilotSessions.CourierServiceTests.Client;

namespace Eden.Protocol.Tests.Fittings;

public sealed class DeveloperFittingTests
{
    [Fact]
    public void NativeFitChargeOnlineSetupAndUnloadUseTheResidentFittingAndCargoOwners()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        using Client gm = new(f.Db, f.Clock, 1000001, PilotRole.GameMasterLow);
        gm.Refresh();
        using Client player = new(f.Db, f.Clock, 1000001);
        player.Refresh();
        Assert.IsType<PilotCallResult.Failure>(Call(player, "/unload me all"));
        Assert.Equal(PilotCallResult.None, Call(gm, "/unload me all"));
        Assert.Equal(PilotCallResult.None, Call(gm, "/fit \"me\" 561 2"));
        var guns = f
            .Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId)
            .Where(item => item.TypeId == 561)
            .OrderBy(item => item.FlagId)
            .ToArray();
        Assert.Equal(new[] { (int)InventoryFlag.HighSlot0, (int)InventoryFlag.HighSlot1 }, guns.Select(gun => gun.FlagId));
        f.Fitting.SetOnline(f.Pilot.PilotId, f.Pilot.ShipId, guns[0].Id, false);
        Assert.Equal(PilotCallResult.None, Call(gm, "/online me"));
        Assert.All(f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId).Where(item => item.TypeId == 561), item => Assert.True(item.Online));
        Assert.Equal(PilotCallResult.None, Call(gm, "/fit me 222 15 flag=27"));
        Assert.Equal(15, Assert.Single(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId)).Quantity);
        var setup = Assert.IsType<PilotCallResult.Success>(Call(gm, "/getshipsetup me"));
        Assert.Equal("DNA:601:561:561", setup.Reply.ToBlue());
        Assert.Equal(PilotCallResult.None, Call(gm, $"/unload me {guns[0].Id}"));
        Assert.Empty(f.Ammo.Read(f.Pilot.PilotId, f.Pilot.ShipId));
        Assert.Contains(f.Inventory.List(f.Pilot.PilotId, f.Pilot.DockedLocationId), item => item.TypeId == 222 && item.Quantity == 15);
        var before = f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId);
        Assert.IsType<PilotCallResult.Failure>(Call(gm, "/fit me 561 2"));
        Assert.Equal(before, f.Inventory.List(f.Pilot.PilotId, f.Pilot.ShipId));
    }

    private static PilotCallResult Call(Client client, string command) => client.Call("SlashCmd", [command], "slash");
}
