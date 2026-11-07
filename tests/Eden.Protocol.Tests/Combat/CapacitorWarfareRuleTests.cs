using Eden.Gameplay.Combat;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;

namespace Eden.Protocol.Tests.Combat;

public sealed class CapacitorWarfareRuleTests
{
    private static CapacitorModule Module(CapacitorModuleKind kind, double resolution = 0) =>
        new(
            1,
            2,
            kind switch
            {
                CapacitorModuleKind.Transfer => DogmaEffect.RemoteCapacitorTransfer,
                CapacitorModuleKind.Neutralizer => DogmaEffect.EnergyNeutralizer,
                _ => DogmaEffect.EnergyNosferatu,
            },
            kind,
            5000,
            kind == CapacitorModuleKind.Nosferatu ? 0 : 20,
            50,
            5000,
            10000,
            resolution
        );

    [Theory]
    [InlineData(CapacitorModuleKind.Transfer, 80, 250)]
    [InlineData(CapacitorModuleKind.Neutralizer, 80, 150)]
    [InlineData(CapacitorModuleKind.Nosferatu, 150, 150)]
    public void EachKindPaysOnceAndChangesTheCorrectCapacitorPools(CapacitorModuleKind kind, double source, double target)
    {
        var result = CapacitorWarfareRules.Apply(Module(kind), new(100, 1000), new(200, 1000), 5000, 40, 1);
        Assert.Equal(new(source, target, 50), result);
    }

    [Theory]
    [InlineData(99, false, 0)]
    [InlineData(100, false, 0)]
    [InlineData(200, false, 50)]
    [InlineData(20, true, 20)]
    [InlineData(0, true, 0)]
    public void NosferatuUsesAbsoluteChargeAndHonorsTheBloodRaiderOverride(double target, bool bypass, double amount)
    {
        // Source percentage is lower than the 99-GJ target, but its amount is higher.
        var result = CapacitorWarfareRules.Apply(
            Module(CapacitorModuleKind.Nosferatu) with
            {
                NosOverride = bypass,
            },
            new(100, 2000),
            new(target, 250),
            0,
            40,
            1
        );
        Assert.Equal(amount, result.Amount);
        Assert.Equal(100 + amount, result.Source);
        Assert.Equal(target - amount, result.Target);
    }

    [Theory]
    [InlineData(CapacitorModuleKind.Transfer, 80, 1000, 2)]
    [InlineData(CapacitorModuleKind.Neutralizer, 80, 0, 2)]
    [InlineData(CapacitorModuleKind.Nosferatu, 100, 0, 2)]
    public void EmptyTargetsAndFullRecipientsDoNotRefundOrCreateExtraEnergy(
        CapacitorModuleKind kind,
        double source,
        double target,
        double amount
    )
    {
        var input = kind == CapacitorModuleKind.Transfer ? new CapacitorPool(998, 1000) : new CapacitorPool(2, 1000);
        var result = CapacitorWarfareRules.Apply(Module(kind) with { NosOverride = true }, new(100, 100), input, 0, 40, 1);
        Assert.Equal(new(source, target, amount), result);
    }

    [Theory]
    [InlineData(5000, 8000, 1, 50)]
    [InlineData(15000, 8000, 1, 25)]
    [InlineData(25000, 8000, 1, 3.125)]
    [InlineData(5000, 400, 1, 2.5)]
    [InlineData(15000, 400, .5, .625)]
    [InlineData(5000, 40000, 0, 0)]
    public void CapitalSignatureResolutionFalloffAndResistanceMultiply(double distance, double signature, double resistance, double amount)
    {
        var result = CapacitorWarfareRules.Apply(
            Module(CapacitorModuleKind.Neutralizer, 8000),
            new(100, 1000),
            new(200, 1000),
            distance,
            signature,
            resistance
        );
        Assert.Equal(amount, result.Amount, 9);
        Assert.Equal(80, result.Source);
        Assert.Equal(200 - amount, result.Target, 9);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidInputsRejectInsteadOfSavingNonfiniteOrNegativeResources(double invalid)
    {
        var module = Module(CapacitorModuleKind.Neutralizer);
        CapacitorPool pool = new(100, 1000);
        Assert.Throws<InvalidDataException>(() => CapacitorWarfareRules.Apply(module, pool, pool, invalid, 40, 1));
        Assert.Throws<InvalidDataException>(() => CapacitorWarfareRules.Apply(module, pool, pool, 0, invalid, 1));
        Assert.Throws<InvalidDataException>(() => CapacitorWarfareRules.Apply(module, pool, pool, 0, 40, invalid));
        Assert.Throws<InvalidDataException>(() =>
            CapacitorWarfareRules.Apply(module with { CapacitorNeed = invalid }, pool, pool, 0, 40, 1)
        );
        Assert.Throws<InvalidDataException>(() => CapacitorWarfareRules.Apply(module, pool with { Charge = invalid }, pool, 0, 40, 1));
    }

    [Fact]
    public void InsufficientSourceCapacitorRejectsEvenWhenTheTargetWouldBeEmptyOrImmune()
    {
        Assert.Throws<InvalidDataException>(() =>
            CapacitorWarfareRules.Apply(Module(CapacitorModuleKind.Neutralizer), new(19, 100), new(0, 100), 0, 40, 0)
        );
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 1000)]
    [InlineData(50, 2100)]
    [InlineData(1000, 2100)]
    [InlineData(1000, 1000000)]
    [InlineData(2000, 1000)]
    [InlineData(-1, 1000)]
    public void ZeroElapsedRechargePreservesExactSavedChargeAndCapacityClamping(double charge, double capacity)
    {
        Assert.Equal(Math.Clamp(charge, 0, capacity), ChargedResource.Recharged(charge, capacity, 10000, 0));
        Assert.Equal(Math.Clamp(charge, 0, capacity), ChargedResource.Recharged(charge, capacity, 10000, -1));
        Assert.True(ChargedResource.Recharged(Math.Clamp(charge, 0, capacity), capacity, 10000, .5) >= Math.Clamp(charge, 0, capacity));
    }
}
