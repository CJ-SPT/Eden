using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Repair;

namespace Eden.Protocol.Tests.Repair;

public sealed class MutadaptiveRepairRuleTests
{
    private static Dictionary<DogmaAttributeId, double> Values() =>
        new()
        {
            [DogmaAttributeId.Duration] = 6000,
            [DogmaAttributeId.CapacitorNeed] = 465,
            [DogmaAttributeId.ArmorRepairAmount] = 392,
            [DogmaAttributeId.MaxRange] = 10500,
            [DogmaAttributeId.RepairMultiplierBonusPerCycle] = .1,
            [DogmaAttributeId.RepairMultiplierBonusMax] = 1.5,
        };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1.1)]
    [InlineData(14, 2.4)]
    [InlineData(15, 2.5)]
    [InlineData(16, 2.5)]
    [InlineData(int.MaxValue, 2.5)]
    public void AdditionalCyclesAddBaseAmountAndStopAtTheCatalogCap(int cycles, double multiplier)
    {
        var module = RemoteRepairRules.Define(1, 49770, DogmaEffect.MutadaptiveRemoteArmorRepair, Values());
        var ramped = RemoteRepairRules.Ramp(module, cycles);
        Assert.Equal(392 * multiplier, ramped.Amount, 8);
        Assert.Equal(392, module.Amount);
        Assert.Equal(392 * multiplier * .4, RemoteRepairRules.Amount(ramped, 10500, .4), 8);
        Assert.Equal(0, RemoteRepairRules.Amount(ramped, 10500.001, 1));
    }

    [Theory]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusPerCycle, 0)]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusPerCycle, double.NaN)]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusPerCycle, -1)]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusMax, 0)]
    [InlineData(DogmaAttributeId.RepairMultiplierBonusMax, double.PositiveInfinity)]
    [InlineData(DogmaAttributeId.FalloffEffectiveness, 1)]
    public void InvalidRampsAndNonzeroFalloffAreRejected(DogmaAttributeId attribute, double value)
    {
        var values = Values();
        values[attribute] = value;
        Assert.Throws<InvalidDataException>(() => RemoteRepairRules.Define(1, 49770, DogmaEffect.MutadaptiveRemoteArmorRepair, values));
    }

    [Fact]
    public void UnpaidNegativeStepsOrdinaryModulesAndOverflowCannotProduceRepair()
    {
        var values = Values();
        var module = RemoteRepairRules.Define(1, 49770, DogmaEffect.MutadaptiveRemoteArmorRepair, values);
        Assert.Throws<InvalidDataException>(() => RemoteRepairRules.Ramp(module, -1));
        Assert.Throws<InvalidDataException>(() => RemoteRepairRules.Ramp(module with { Effect = DogmaEffect.RemoteArmorRepair }, 1));
        values[DogmaAttributeId.ArmorRepairAmount] = double.MaxValue;
        module = RemoteRepairRules.Define(1, 49770, DogmaEffect.MutadaptiveRemoteArmorRepair, values);
        Assert.Throws<InvalidDataException>(() => RemoteRepairRules.Ramp(module, 15));
    }
}
