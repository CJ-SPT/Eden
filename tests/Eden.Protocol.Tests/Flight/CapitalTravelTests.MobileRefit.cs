using Eden.Data.World.Combat;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.World.Fittings;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FittedModuleTogglesAwayFromServiceAndOnlyOnliningChargesCapacitor(bool godmaEffect)
    {
        using Fixture fixture = new(armed: true);
        long module = fixture.Gun!.Value;
        using Client miner = fixture.Select(1000002);
        var ship = miner.Pilot.ShipId;
        ClearFittingTestCargo(fixture, miner.Pilot.PilotId, ship);
        double capacity = Resources().Attributes[DogmaAttributeId.CapacitorCapacity];
        SetCapacitor(capacity * 0.94);
        Assert.Same(PilotCallResult.None, Toggle(online: false));
        Assert.False(Item().Online);
        Assert.Equal(capacity * 0.94, Resources().Capacitor, 8);

        miner.Context.Notifications.Clear();
        var insufficient = Assert.IsType<PilotCallResult.NamedFailure>(Toggle(online: true));
        Assert.Equal("NotEnoughCapacitorForOnline", insufficient.Name);
        var values = Assert.IsType<Dictionary<string, object?>>(insufficient.Values.ToBlue());
        Assert.Equal(Item().TypeId, Assert.IsType<int>(values["module"]));
        Assert.Equal(94, Assert.IsType<double>(values["have"]), 8);
        Assert.Equal(95, Assert.IsType<int>(values["need"]));
        Assert.False(Item().Online);
        Assert.Equal(capacity * 0.94, Resources().Capacitor, 8);
        Assert.Empty(miner.Context.Notifications);

        SetCapacitor(capacity * 0.95);
        Assert.Same(PilotCallResult.None, Toggle(online: true));
        Assert.True(Item().Online);
        Assert.Equal(capacity * 0.33, Resources().Capacitor, 8);
        Assert.Contains(miner.Context.Notifications.OfType<ShipEffectChanged>(), e => e.Effect.ModuleId == module && e.Active);
        miner.Context.Notifications.Clear();
        Assert.Same(PilotCallResult.None, Toggle(online: true));
        Assert.Equal(capacity * 0.33, Resources().Capacitor, 8);
        Assert.Empty(miner.Context.Notifications.OfType<ShipEffectChanged>());

        miner.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.True(Item().Online);
        Assert.Equal(capacity * 0.33, Resources().Capacitor, 8);

        InventoryItem Item() => Assert.Single(fixture.Inventory.List(miner.Pilot.PilotId, ship), item => item.Id == module);
        ShipResourceSnapshot Resources() =>
            fixture.Db.Services.GetRequiredService<IPilotShipResources>().Read(miner.Pilot.PilotId, ship, fixture.Clock.Now);
        PilotCallResult Toggle(bool online) =>
            godmaEffect
                ? online
                    ? miner.Call("dogmaIM", "Activate", module, InventoryWireDefinitions.OnlineEffectName, null, 0L)
                    : miner.Call("dogmaIM", "Deactivate", module, InventoryWireDefinitions.OnlineEffectName)
                : miner.Call("dogmaIM", online ? "SetModuleOnline" : "TakeModuleOffline", ship, module);

        void SetCapacitor(double amount)
        {
            using var action = fixture.Live.Economy.Begin(new(ships: [ship]));
            ShipResources.SetCapacitor(action, ship, amount, fixture.Clock.Now);
            action.Commit();
        }
    }

    [Fact]
    public async Task VentureRefitsAtRorqualAndRespectsRangeAndWeaponsDeadline()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen, armed: true);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var guest = fixture.Db.Store.FindPilot(1000002)!;
        var point = new SpacePosition(10_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, point, Jan);
        fixture.Db.Store.SetPosition(guest.ShipId, point + new SpacePosition(10_000, 0, 0), Jan);
        ClearFittingTestCargo(fixture, guest.PilotId, guest.ShipId);
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
        var resources = fixture.Db.Services.GetRequiredService<IPilotShipResources>();
        double capacitor = resources.Read(guest.PilotId, guest.ShipId, fixture.Clock.Now).Capacitor / 2;
        using (var action = fixture.Live.Economy.Begin(new(ships: [guest.ShipId])))
        {
            ShipResources.SetCapacitor(action, guest.ShipId, capacitor, fixture.Clock.Now);
            action.Commit();
        }
        Assert.Same(PilotCallResult.None, miner.Call("dogmaIM", "SetModuleOnline", guest.ShipId, module));
        Assert.True(Item().Online);
        Assert.Equal(capacitor, resources.Read(guest.PilotId, guest.ShipId, fixture.Clock.Now).Capacitor, 8);

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
        Assert.Same(PilotCallResult.None, miner.Call("dogmaIM", "TakeModuleOffline", guest.ShipId, module));
        var belowThreshold = Assert.IsType<PilotCallResult.NamedFailure>(miner.Call("dogmaIM", "SetModuleOnline", guest.ShipId, module));
        Assert.Equal("NotEnoughCapacitorForOnline", belowThreshold.Name);
        Assert.False(Item().Online);
        fixture.Clock.Milliseconds += 2000;
        Assert.Same(PilotCallResult.None, miner.Call("dogmaIM", "SetModuleOnline", guest.ShipId, module));
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

    private static void ClearFittingTestCargo(Fixture fixture, long pilot, long ship)
    {
        using var action = fixture.Live.Economy.Begin(new(pilots: [pilot], ships: [ship]));
        action.Remove(action.Set<OwnedItem>().Single(i => i.LocationId == ship && i.TypeId == LiquidOzone));
        action.Commit();
    }
}
