using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class PvpResourceTests
{
    [Fact]
    public void AmmunitionAccountsForRangeTrackingReloadCostAndOwnedQuantity()
    {
        CombatWeapon close = new(1, 561, 222, new(0, 0, 8, 4), 3000, 3000, 3000, .3, 40, 1);
        var far = close with { ChargeTypeId = 215, Damage = new(0, 0, 3, 2), Optimal = 10_000 };
        PvpAmmunitionOption[] options = [new(10, 215, 20, far), new(11, 222, 20, close)];
        Assert.Equal(10, PvpResources.Ammunition(close, true, options, 12_000, 0, 40, 5000));
        Assert.Equal(11, PvpResources.Ammunition(far, true, options, 1000, 0, 40, 5000));
        Assert.Equal(0, PvpResources.Ammunition(far, true, options, 1000, 0, 40, 25_000));
        Assert.Equal(0, PvpResources.Ammunition(close, true, options, 1000, 0, 40, 5000));
        Assert.Equal(0, PvpResources.Ammunition(close, false, [options[0] with { Quantity = 0 }], 1000, 0, 40, 5000));
        var poorTracking = close with { Tracking = .003, Damage = new(0, 0, 30, 0) };
        Assert.Equal(0, PvpResources.Ammunition(close, true, [new(20, 500, 20, poorTracking)], 1000, .05, 40, 5000));
        Assert.Equal(11, PvpResources.Ammunition(close, false, options, 1000, 0, 40, 5000));
    }

    [Fact]
    public void HeatStopsAtItsSavedBudgetRackTemperatureAndNeighborDamageLimits()
    {
        var rack = new HeatRackProfile(ModuleRack.Medium, 100, .01, 1, .5);
        var module = new HeatDamageModule(10, ModuleRack.Medium, 0, true, new(10, 40, 0), 2);
        ShipHeatSnapshot heat = new(0, [new(rack, 0)], []) { Modules = [module] };
        bool Allowed(ShipHeatSnapshot state, int cycles = 0) =>
            PvpResources.Heat(DogmaEffect.WarpDisruptor, cycles, state, 10, false, 22_000, 20_000, 24_000);
        Assert.True(Allowed(heat));
        Assert.False(Allowed(heat, PvpResources.MaximumHeatedCycles));
        Assert.False(Allowed(heat with { Racks = [new(rack, 45)] }));
        Assert.False(Allowed(heat with { Modules = [module, module with { ItemId = 11, Condition = new(11, 40, 14) }] }));
        Assert.False(Allowed(heat with { Modules = [module with { Online = false }] }));
        Assert.False(PvpResources.Heat(DogmaEffect.WarpDisruptor, 0, heat, 10, false, 10_000, 20_000, 24_000));
        Assert.False(PvpResources.Heat(DogmaEffect.WarpDisruptor, 0, heat, 10, false, 25_000, 20_000, 24_000));
        Assert.True(PvpResources.Heat(DogmaEffect.ProjectileFired, 0, heat, 10, true, 500, 0, 0));
        Assert.False(PvpResources.Heat(DogmaEffect.ProjectileFired, 0, heat, 10, false, 500, 0, 0));
    }
}
