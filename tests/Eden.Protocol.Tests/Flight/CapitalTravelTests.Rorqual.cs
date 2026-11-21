using Eden.Data.World.Mining;
using Eden.Gameplay.Docking;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Drones;
using Eden.Gameplay.Flight;
using Eden.Gameplay.Inventory;
using Eden.Protocol.PilotSessions;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Flight;

public sealed partial class CapitalTravelTests
{
    private const int HeavyWater = 16272;

    [Fact]
    public async Task RorqualPaysForIndustrialCoreAndImprovesExcavatorYield()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen, Rapier, CapitalTravelRules.StandardGenerator);
        long core = fixture.FitCapitalModule(CapitalModeRules.IndustrialCoreOne);
        _ = fixture.PutInTrainedHold(HeavyWater, 902, InventoryFlag.FuelBay);
        _ = fixture.PutInTrainedHold(UtilityDroneRules.ExcavatorOre, 1, InventoryFlag.DroneBay);
        var pilot = fixture.Db.Store.FindPilot(1000001)!;
        fixture.Db.Store.SetPosition(pilot.ShipId, new(1_000_000_000, 0, 0), Jan);
        using Client rorqual = fixture.Select(1000001);
        var drones = fixture.Db.Services.GetRequiredService<IPilotDrones>();
        var ordinary = drones.Profile(pilot.PilotId, UtilityDroneRules.ExcavatorOre)!.Utility!;
        Assert.IsType<PilotCallResult.Failure>(Activate());
        Assert.Equal(902, Quantity(fixture, rorqual, HeavyWater));
        Assert.Null(fixture.Travel.Mode(pilot.ShipId));

        _ = fixture.PutInTrainedHold(HeavyWater, 50, InventoryFlag.Cargo);
        var activated = Activate();
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        var mode = Assert.IsType<CapitalModeCycle>(fixture.Travel.Mode(pilot.ShipId));
        Assert.Equal(49, Quantity(fixture, rorqual, HeavyWater));
        var enhanced = drones.Profile(pilot.PilotId, UtilityDroneRules.ExcavatorOre)!.Utility!;
        Assert.Equal(ordinary.Amount * 2.7, enhanced.Amount, 6);
        Assert.Equal(ordinary.DurationMilliseconds, enhanced.DurationMilliseconds);
        var warp = fixture.Db.Services.GetRequiredService<IPilotWarpDrive>();
        Assert.Throws<InvalidDataException>(() => warp.Activate(pilot.PilotId, pilot.ShipId, 1_000_000, fixture.Clock.Now));

        await rorqual.TickAsync();
        rorqual.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.Equal(mode, fixture.Travel.Mode(pilot.ShipId));
        using Client reopened = fixture.Select(1000001);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(mode.EndsAt - fixture.Clock.Now));
        await reopened.TickAsync();
        Assert.Null(fixture.Travel.Mode(pilot.ShipId));
        Assert.Equal(ordinary.Amount, drones.Profile(pilot.PilotId, UtilityDroneRules.ExcavatorOre)!.Utility!.Amount, 6);

        PilotCallResult Activate() => rorqual.Call("dogmaIM", "Activate", core, "industrialCoreEffect2", null, 0);
    }

    [Fact]
    public async Task RorqualAdvertisesPaidFleetCompressionAndRejectsAtTheExactDeadline()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        long core = fixture.FitCapitalModule(CapitalModeRules.IndustrialCoreOne);
        long compressor = fixture.FitCapitalModule(ShipCompressionRules.CapitalAsteroidOre);
        _ = fixture.PutInTrainedHold(HeavyWater, 1200, InventoryFlag.FuelBay);
        const int veldspar = 1230;
        const int compressed = 62516;
        const int units = 100;
        long ore = fixture.PutInEscortHold(veldspar, units, InventoryFlag.GeneralMiningHold);
        long reserve = fixture.PutInEscortHold(veldspar, units, InventoryFlag.Cargo);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var miner = fixture.Db.Store.FindPilot(1000002)!;
        SpacePosition origin = new(1_000_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(miner.ShipId, origin + new SpacePosition(40_000, 0, 0), Jan);
        using Client rorqual = fixture.Select(1000001);
        using Client venture = fixture.Select(1000002);
        Assert.IsType<PilotCallResult.Failure>(Cycle());
        Assert.IsType<PilotCallResult.Success>(rorqual.Call("dogmaIM", "Activate", core, "industrialCoreEffect2", null, 0));
        var started = Cycle();
        Assert.True(started is PilotCallResult.Success, started.ToString());
        var paid = Assert.Single(fixture.Travel.Compressors(owner.ShipId));
        Assert.Equal(144_000, paid.Facility.Range);
        Assert.Equal(334, paid.Facility.TypeList);
        Assert.IsType<PilotCallResult.Failure>(Compress(ore));
        Assert.Equal(veldspar, Item(ore).TypeId);
        fixture.Join(venture);
        await rorqual.TickAsync();
        await venture.TickAsync();
        Assert.Contains(venture.Packets, packet => ContainsText(packet, "compression_facility_typelists"));
        var result = Compress(ore);
        Assert.True(result is PilotCallResult.Success, result.ToString());
        Assert.Equal(compressed, Item(ore).TypeId);
        Assert.Equal(units, Item(ore).Quantity);
        Assert.Equal((int)InventoryFlag.GeneralMiningHold, Item(ore).FlagId);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(paid.EndsAt - fixture.Clock.Now));
        Assert.IsType<PilotCallResult.Failure>(Compress(reserve));
        Assert.Equal(veldspar, Item(reserve).TypeId);
        Assert.Empty(fixture.Travel.Compressors(owner.ShipId));

        PilotCallResult Cycle() => rorqual.Call("dogmaIM", "Activate", compressor, "industrialItemCompression", null, 0);

        PilotCallResult Compress(long item) => venture.Call("inSpaceCompressionMgr", "CompressItemInSpace", item, owner.ShipId);

        InventoryItem Item(long id) => fixture.Inventory.List(miner.PilotId, miner.ShipId).Single(item => item.Id == id);
    }

    [Fact]
    public async Task RorqualPanicRequiresLockedAsteroidBurnsOutAndProtectsItsFleet()
    {
        using Fixture fixture = new(UtilityDroneRules.Rorqual, Oxygen);
        long panic = fixture.FitCapitalModule(IndustrialProtectionRules.Module);
        var owner = fixture.Db.Store.FindPilot(1000001)!;
        var miner = fixture.Db.Store.FindPilot(1000002)!;
        var origin = new SpacePosition(1_000_000_000, 0, 0);
        fixture.Db.Store.SetPosition(owner.ShipId, origin, Jan);
        fixture.Db.Store.SetPosition(miner.ShipId, origin + new SpacePosition(10_000, 0, 0), Jan);
        const long rock = 8_000_000_000_201;
        using (var action = fixture.Live.Economy.Begin(new(systems: [Jan])))
        {
            action.Add(
                new WorldAsteroid
                {
                    Id = rock,
                    TypeId = 1230,
                    SystemId = Jan,
                    Quantity = 2000,
                    BeltId = 40088235,
                    X = origin.X + 5000,
                    Radius = 300,
                }
            );
            action.Commit();
        }
        using Client rorqual = fixture.Select(1000001);
        using Client venture = fixture.Select(1000002);
        fixture.Join(venture);
        var fitting = fixture.Db.Services.GetRequiredService<IPilotFitting>();
        double before = Resistance();
        Assert.IsType<PilotCallResult.Failure>(Activate());
        await rorqual.TickAsync();
        var locking = rorqual.Call("dogmaIM", "AddTarget", rock);
        Assert.True(locking is PilotCallResult.Success, locking.ToString());
        fixture.Clock.Seconds += 60;
        await rorqual.TickAsync();
        var activated = Activate();
        Assert.True(activated is PilotCallResult.Success, activated.ToString());
        Assert.False(fixture.Inventory.List(owner.PilotId, owner.ShipId).Single(item => item.Id == panic).Online);
        Assert.True(fixture.Travel.IndustrialProtectionActive(owner.ShipId));
        Assert.True(fixture.Travel.IndustrialProtectionActive(miner.ShipId));
        Assert.Equal(before * 0.0001, Resistance(), 8);
        var warp = fixture.Db.Services.GetRequiredService<IPilotWarpDrive>();
        Assert.Throws<InvalidDataException>(() => warp.Activate(miner.PilotId, miner.ShipId, 1_000_000, fixture.Clock.Now));
        long ends = fixture.Clock.Now + 264 * TimeSpan.TicksPerSecond;

        rorqual.Dispose();
        venture.Dispose();
        await fixture.Live.FlushAsync();
        fixture.Reopen(restart: true);
        Assert.True(fixture.Travel.IndustrialProtectionActive(miner.ShipId));
        Assert.Equal(before * 0.0001, Resistance(), 8);
        using Client reopened = fixture.Select(1000002);
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(ends - fixture.Clock.Now - 1));
        Assert.True(fixture.Travel.IndustrialProtectionActive(miner.ShipId));
        fixture.Clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        Assert.False(fixture.Travel.IndustrialProtectionActive(miner.ShipId));
        await reopened.TickAsync();
        Assert.Equal(before, Resistance(), 8);

        PilotCallResult Activate() => rorqual.Call("dogmaIM", "Activate", panic, "moduleBonusIndustrialInvulnerability", null, 0);
        double Resistance() => fitting.ShipAttributes(miner.PilotId)[DogmaAttributeId.ShieldEmResonance];
    }
}
