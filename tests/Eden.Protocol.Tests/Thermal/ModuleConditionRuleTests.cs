using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class ModuleConditionRuleTests
{
    [Fact]
    public void DamageSaturatesAtHitPointsAndRepairDoesNotDiscardCapacityChangeDamage()
    {
        var healthy = ModuleConditionRules.Define(1, 40, 0);
        var damaged = ModuleConditionRules.ApplyDamage(healthy, 10);
        Assert.Equal(10, damaged.Damage);
        Assert.False(damaged.BurnedOut);
        Assert.Equal(0, healthy.Damage);
        var burned = ModuleConditionRules.ApplyDamage(damaged, double.MaxValue);
        Assert.Equal(40, burned.Damage);
        Assert.True(burned.BurnedOut);
        Assert.Equal(burned, ModuleConditionRules.ApplyDamage(burned, 1));
        var reducedCapacity = ModuleConditionRules.Define(1, 20, burned.Damage);
        Assert.Equal(40, ModuleConditionRules.ApplyDamage(reducedCapacity, 5).Damage);
        Assert.True(ModuleConditionRules.Repair(reducedCapacity, 20).BurnedOut);
        var repaired = ModuleConditionRules.Repair(reducedCapacity, 21);
        Assert.Equal(19, repaired.Damage);
        Assert.False(repaired.BurnedOut);
        Assert.Equal(0, ModuleConditionRules.Repair(repaired, double.MaxValue).Damage);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    public void InvalidAmountsAndDamageCannotReachSavedCondition(double invalid)
    {
        var healthy = ModuleConditionRules.Define(1, 40, 0);
        Assert.Throws<InvalidDataException>(() => ModuleConditionRules.ApplyDamage(healthy, invalid));
        Assert.Throws<InvalidDataException>(() => ModuleConditionRules.Repair(healthy, invalid));
        Assert.Throws<InvalidDataException>(() => ModuleConditionRules.Define(1, 40, invalid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidHitPointsRejectCondition(double hitPoints) =>
        Assert.Throws<InvalidDataException>(() => ModuleConditionRules.Define(1, hitPoints, 0));
}
