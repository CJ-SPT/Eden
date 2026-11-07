using Eden.Gameplay.Dogma;
using Eden.Gameplay.Repair;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Tests.Repair;

public sealed class AncillaryRepairRuleTests
{
    private static Dictionary<DogmaAttributeId, double> ArmorValues() =>
        new()
        {
            [DogmaAttributeId.Duration] = 12000,
            [DogmaAttributeId.ArmorRepairAmount] = 207,
            [DogmaAttributeId.CapacitorNeed] = 160,
            [DogmaAttributeId.ChargeRate] = 4,
            [DogmaAttributeId.ChargedArmorMultiplier] = 3,
            [DogmaAttributeId.ReloadTime] = 60000,
        };

    [Theory]
    [InlineData(0, 207, 0)]
    [InlineData(1, 310.5, 1)]
    [InlineData(2, 414, 2)]
    [InlineData(3, 517.5, 3)]
    [InlineData(4, 621, 4)]
    [InlineData(5, 621, 4)]
    public void PartialPasteConsumesRemainingFuelAndScalesOnlyTheBonus(int quantity, double amount, int used)
    {
        var module = AncillaryRepairRules.Define(AncillaryRepairKind.Armor, ArmorValues());
        var paid = AncillaryRepairRules.Payment(module, quantity);
        Assert.Equal(amount, paid.Amount, 8);
        Assert.Equal(160, paid.CapacitorNeed);
        Assert.Equal(used, paid.ChargesUsed);
        Assert.Equal(207, module.Amount);
    }

    [Theory]
    [InlineData(0, 66, 0)]
    [InlineData(1, 0, 1)]
    [InlineData(9, 0, 1)]
    public void ShieldChargesReplaceCapacitorPaymentWithoutChangingRepairAmount(int quantity, double cost, int used)
    {
        var values = ArmorValues();
        values[DogmaAttributeId.ShieldBonus] = 52;
        values[DogmaAttributeId.CapacitorNeed] = 66;
        values[DogmaAttributeId.ChargeRate] = 1;
        var module = AncillaryRepairRules.Define(AncillaryRepairKind.Shield, values);
        Assert.Equal(new(52, cost, used), AncillaryRepairRules.Payment(module, quantity));
    }

    [Theory]
    [InlineData(DogmaAttributeId.Duration, 0)]
    [InlineData(DogmaAttributeId.ArmorRepairAmount, double.NaN)]
    [InlineData(DogmaAttributeId.CapacitorNeed, -1)]
    [InlineData(DogmaAttributeId.ChargeRate, 0)]
    [InlineData(DogmaAttributeId.ChargeRate, 1.5)]
    [InlineData(DogmaAttributeId.ChargedArmorMultiplier, .5)]
    [InlineData(DogmaAttributeId.ReloadTime, -1)]
    public void InvalidFuelProfilesCannotProduceAPayment(DogmaAttributeId attribute, double value)
    {
        var values = ArmorValues();
        values[attribute] = value;
        Assert.Throws<InvalidDataException>(() => AncillaryRepairRules.Define(AncillaryRepairKind.Armor, values));
    }

    [Fact]
    public void FuelSizesAreExactAndSmallShieldSizeZeroIsNotAnAbsentProfile()
    {
        Dictionary<DogmaAttributeId, double> module = new() { [DogmaAttributeId.ChargeGroup2] = CapacitorBoosterDefinitions.ChargeGroup };
        Assert.True(
            AncillaryRepairRules.Compatible(
                AncillaryRepairKind.Shield,
                CapacitorBoosterDefinitions.ChargeGroup,
                module,
                new Dictionary<DogmaAttributeId, double>()
            )
        );
        module[DogmaAttributeId.ChargeSize] = 1;
        Assert.False(
            AncillaryRepairRules.Compatible(
                AncillaryRepairKind.Shield,
                CapacitorBoosterDefinitions.ChargeGroup,
                module,
                new Dictionary<DogmaAttributeId, double>()
            )
        );
        Dictionary<DogmaAttributeId, double> charge = new() { [DogmaAttributeId.ChargeSize] = 1 };
        Assert.True(AncillaryRepairRules.Compatible(AncillaryRepairKind.Shield, CapacitorBoosterDefinitions.ChargeGroup, module, charge));
        module[DogmaAttributeId.ChargeGroup2] = AncillaryRepairRules.PasteGroup;
        Assert.True(AncillaryRepairRules.Compatible(AncillaryRepairKind.Armor, AncillaryRepairRules.PasteGroup, module, charge));
        Assert.False(AncillaryRepairRules.Compatible(AncillaryRepairKind.Shield, AncillaryRepairRules.PasteGroup, module, charge));
    }

    [Fact]
    public void InvalidQuantitiesProfilesAndOverflowCannotProduceAnUnboundedPaidRepair()
    {
        var module = AncillaryRepairRules.Define(AncillaryRepairKind.Armor, ArmorValues());
        Assert.Throws<InvalidDataException>(() => AncillaryRepairRules.Payment(module, -1));
        Assert.Throws<InvalidDataException>(() => AncillaryRepairRules.Payment(module with { ChargesPerCycle = 0 }, 1));
        Assert.Throws<InvalidDataException>(() => AncillaryRepairRules.Payment(module with { Amount = double.MaxValue }, 4));
    }
}
