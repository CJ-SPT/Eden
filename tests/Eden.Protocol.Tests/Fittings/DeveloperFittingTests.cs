using Eden.Data.World.Inventory;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Tests.Ammunition;
using Eden.World.Runtime;
using Eden.World.Storage;
using Microsoft.Extensions.DependencyInjection;
using Client = Eden.Protocol.Tests.PilotSessions.CourierServiceTests.Client;

namespace Eden.Protocol.Tests.Fittings;

public sealed class DeveloperFittingTests
{
    [Fact]
    public async Task CopycatClonesTheDockedHullWithAnIndependentFittingAndMagazine()
    {
        using AmmunitionTests.Fixture f = new(liveWorld: true);
        using Client gm = new(f.Db, f.Clock, 1000001, PilotRole.WorldMod);
        gm.Refresh();
        var pilot = f.Pilot;
        f.Load(quantity: 35);
        Assert.IsType<PilotCallResult.Failure>(Call(gm, $"/copyship {pilot.ShipId + 1} 1"));
        Assert.IsType<PilotCallResult.Failure>(Call(gm, $"/copyship {pilot.ShipId} 2"));
        var fleet = f.Db.Services.GetRequiredService<IPilotFleet>();
        var stationBefore = fleet.Ships(pilot.PilotId, pilot.DockedLocationId);
        using (Client player = new(f.Db, f.Clock, 1000001))
        {
            player.Refresh();
            Assert.IsType<PilotCallResult.Failure>(Call(player, $"/copyship {pilot.ShipId} 1"));
        }
        Assert.Equal(stationBefore, fleet.Ships(pilot.PilotId, pilot.DockedLocationId));
        var result = Assert.IsType<PilotCallResult.Success>(Call(gm, $"/copyship {pilot.ShipId} 1"));
        long clone = Assert.IsType<long>(result.Reply.ToBlue());
        Assert.NotEqual(pilot.ShipId, clone);
        Assert.Equal(pilot.ShipId, f.Pilot.ShipId);
        Assert.Contains(fleet.Ships(pilot.PilotId, pilot.DockedLocationId), row => row.Id == clone && row.Singleton);
        var live = LiveWorld.Find(f.Db.Path)!;
        long clonedModule = live.Economy.Inspect(world =>
        {
            var copied = Assert.Single(world.FittedItems(pilot.PilotId, clone), row => row.TypeId == 561);
            Assert.True(copied.Online);
            Assert.Equal((int)InventoryFlag.HighSlot0, copied.FlagId);
            Assert.Contains(world.LoadedFittingItems(pilot.PilotId, [copied.Id]), row => row.TypeId == 222 && row.Quantity == 35);
            return copied.Id;
        });
        Assert.NotEqual(f.Gun, clonedModule);
        Assert.Equal(35, Assert.Single(f.Ammo.Read(pilot.PilotId, pilot.ShipId)).Quantity);
        await live.FlushAsync();
        using WorldDbContext reopened = new(f.Db.Path);
        Assert.Equal(pilot.DockedLocationId, reopened.Ships.Find(clone)!.DockedLocationId);
        Assert.Equal(clone, reopened.Set<OwnedItem>().Find(clonedModule)!.LocationId);
        Assert.Contains(reopened.Set<OwnedItem>(), row => row.LocationId == clonedModule && row.TypeId == 222 && row.Quantity == 35);
    }

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
