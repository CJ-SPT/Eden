using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class HeatDamageRuleTests
{
    [Fact]
    public void OccupancyAndPhysicalDistanceUseTheDocumentedEnyoExample()
    {
        Assert.Equal(.6 * 11 / 14, HeatDamageRules.Probability(60, 11, 14, .5, 0), 12);
        Assert.Equal(.6 * 11 / 14 * .25, HeatDamageRules.Probability(60, 11, 14, .5, 2), 12);
        Assert.Equal(0, HeatDamageRules.Probability(60, 0, 14, .5, 2));
        Assert.Equal(1, HeatDamageRules.Probability(100, 1, 1, 0, 0));
    }

    [Fact]
    public void AnOfflineNeighborTakesDamageWhileAnotherRackRemainsUntouched()
    {
        var high = new HeatRackProfile(ModuleRack.High, 100, .01, 1, .5);
        var medium = high with { Rack = ModuleRack.Medium };
        var initial = HeatCycleRules.Create([high, medium], 0) with
        {
            Racks = [new(high, 100), new(medium, 100)],
            TotalSlots = 10,
            DamageSeed = 7,
            Modules =
            [
                new(1, ModuleRack.High, 0, true, new(1, 40, 0), 5),
                new(2, ModuleRack.High, 1, false, new(2, 40, 0), 0),
                new(3, ModuleRack.High, 7, true, new(3, 40, 0), 0),
                new(4, ModuleRack.Medium, 0, true, new(4, 40, 0), 0),
            ],
        };
        initial = HeatCycleRules.Start(initial, new(1, ModuleRack.High, 0, 100, .01)).State;
        var final = HeatDamageRules.Advance(initial, 100);
        Assert.Equal(5, final.Modules.Single(m => m.ItemId == 2).Condition.Damage);
        Assert.False(final.Modules.Single(m => m.ItemId == 2).Online);
        Assert.Equal(0, final.Modules.Single(m => m.ItemId == 3).Condition.Damage);
        Assert.Equal(0, final.Modules.Single(m => m.ItemId == 4).Condition.Damage);
        Assert.Equal(.3 * Math.Pow(.5, 7), HeatDamageRules.Probability(100, 3, 10, .5, 7), 12);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SimultaneousBurnoutsDoNotReplayADeadSourceOrChangeWithObserverCount(bool sharedDeadline)
    {
        var profile = new HeatRackProfile(ModuleRack.High, 100, .01, 1, 1);
        var initial = HeatCycleRules.Create([profile], 0) with
        {
            Racks = [new(profile, 100)],
            TotalSlots = 2,
            DamageSeed = 42,
            Modules = [new(1, ModuleRack.High, 0, true, new(1, 40, 0), 40), new(2, ModuleRack.High, 1, true, new(2, 40, 0), 40)],
        };
        initial = HeatCycleRules.Start(initial, new(1, ModuleRack.High, 0, 100, .01)).State;
        initial = HeatCycleRules.Start(initial, new(2, ModuleRack.High, 0, sharedDeadline ? 100 : 200, .01)).State;
        var first = HeatDamageRules.Advance(initial, 100);
        Assert.All(first.Modules, module => Assert.True(module.Condition.BurnedOut));
        Assert.Empty(first.Cycles);
        var late = HeatDamageRules.Advance(initial, 300);
        var stepped = HeatDamageRules.Advance(first, 300);
        Assert.True(HeatCycleRules.SameState(late, stepped));
        Assert.True(HeatCycleRules.SameState(late, HeatDamageRules.Advance(late, 300)));
        Assert.All(initial.Modules, module => Assert.Equal(0, module.Condition.Damage));
    }
}
