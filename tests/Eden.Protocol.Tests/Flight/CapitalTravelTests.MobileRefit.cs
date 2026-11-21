using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Fact]
    public async Task VentureRefitsAtRorqualAndRespectsRangeAndWeaponsDeadline()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen, armed: true);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var guest = fixture.Db.Store.FindPilot(1000002)!;
        var point = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, point, Jan);
        fixture.Db.Store.SetPosition(guest.ShipId, point + new SpacePosition(10_000, 0, 0), Jan);
        using (var action = fixture.Live.Economy.Begin(new(pilots: [guest.PilotId])))
        {
            action.Remove(action.Set<OwnedItem>().Single(i => i.LocationId == guest.ShipId && i.TypeId == LiquidOzone));
            action.Commit();
        }
        long module = fixture.Grant(guest.PilotId, guest.ShipId, InventoryFlag.Cargo, 3638, 1);
        using Client host = fixture.Select(1000001);
        using Client miner = fixture.Select(1000002);
        fixture.Join(miner);
        Assert.IsType<PilotCallResult.Failure>(Move(InventoryFlag.AutoFit));
        Assert.Equal((int)InventoryFlag.Cargo, Item().FlagId);

        var nearby = point + new SpacePosition(6000, 0, 0);
        fixture.Db.Store.SetPosition(guest.ShipId, nearby, Jan);
        fixture.Db.Services.GetRequiredService<IPilotCombatPresence>().Displace(Jan, guest.ShipId, nearby);
        miner.Context.Flight.Checkpoint();
        Assert.Same(PilotCallResult.None, Move(InventoryFlag.AutoFit));
        Assert.True(InventoryDefinitions.IsHighSlot(Item().FlagId));
        Assert.Same(PilotCallResult.None, miner.Call("dogmaIM", "SetModuleOnline", guest.ShipId, module));
        Assert.True(Item().Online);

        using (var action = fixture.Live.Economy.Begin(new(pilots: [guest.PilotId])))
        {
            var timer =
                action.Set<SavedCrimewatch>().Find(guest.PilotId)
                ?? action.Add(new SavedCrimewatch { PilotId = guest.PilotId, SystemId = Jan }).Entity;
            timer.WeaponsUntil = fixture.Clock.Now + TimeSpan.FromSeconds(2).Ticks;
            action.Commit();
        }
        Assert.Contains("weapons timer", Assert.IsType<PilotCallResult.Failure>(Move(InventoryFlag.Cargo)).Message);
        Assert.True(Item().Online);
        fixture.Clock.Milliseconds += 2000;
        Assert.Same(PilotCallResult.None, Move(InventoryFlag.Cargo));
        Assert.Equal((int)InventoryFlag.Cargo, Item().FlagId);
        Assert.False(Item().Online);
        await miner.TickAsync();
        Assert.Contains(miner.Packets, packet => ContainsText(packet, "OnItemChange"));
        miner.Dispose();
        host.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal((int)InventoryFlag.Cargo, Item().FlagId);

        InventoryItem Item() => Assert.Single(fixture.Inventory.List(guest.PilotId, guest.ShipId), item => item.Id == module);
        PilotCallResult Move(InventoryFlag flag) =>
            miner.CallWithKeywords("@ship", "Add", new() { ["flag"] = (long)flag }, module, guest.ShipId);
    }
}
