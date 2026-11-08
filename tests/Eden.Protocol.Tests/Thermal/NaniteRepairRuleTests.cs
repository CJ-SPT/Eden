using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class NaniteRepairRuleTests
{
    internal sealed record Profile(
        int TypeId,
        double BasePrice,
        long PortionSize,
        double HitPoints,
        IReadOnlyList<NaniteMaterialValue> Materials,
        long ComputedBasePrice,
        IReadOnlyList<long> PasteUnits
    );

    internal sealed record Reference(
        int Build,
        string ArchiveSha256,
        string SourceFingerprint,
        double PasteBasePrice,
        IReadOnlyList<Profile> Profiles
    );

    [Fact]
    public void EveryAdmittedModulePriceAndSkillCostMatchesTheIsolatedNativeFunctions()
    {
        var reference = ThermalFixtures.Read<Reference>("NaniteRepairReference.json");
        Assert.Equal(3662168, reference.Build);
        Assert.Equal("37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368", reference.ArchiveSha256);
        Assert.Equal(2766, reference.Profiles.Count(p => p.TypeId > 0));
        Assert.Equal(6, reference.Profiles.Count(p => p.TypeId < 0));
        Assert.Equal(reference.Profiles.Count, reference.Profiles.Select(p => p.TypeId).Distinct().Count());
        int[] levels = [0, 3, 5];
        foreach (var profile in reference.Profiles)
        {
            long price = NaniteRepairRules.ComputedBasePrice(profile.BasePrice, profile.PortionSize, profile.Materials);
            Assert.Equal(profile.ComputedBasePrice, price);
            for (int index = 0; index < levels.Length; index++)
            {
                var quote = NaniteRepairRules.Quote(
                    new(1, profile.HitPoints, profile.HitPoints / 2),
                    price,
                    reference.PasteBasePrice,
                    new(10, .5 * (1 - .05 * levels[index]))
                );
                Assert.Equal(profile.PasteUnits[index], quote.PasteUnits);
            }
        }
    }

    [Fact]
    public void QuotesPreserveNativeCeilingAndExactRepairTimeWithoutChangingCondition()
    {
        ModuleCondition condition = new(1, 40, 10);
        var quote = NaniteRepairRules.Quote(condition, 104000, 6500, new(10, .5));
        Assert.Equal(2, quote.PasteUnits);
        Assert.Equal(TimeSpan.TicksPerMinute, quote.DurationTicks);
        Assert.Same(condition, quote.Condition);
        Assert.Equal(10, condition.Damage);
        var above = NaniteRepairRules.Quote(condition with { Damage = 10.000001 }, 104000, 6500, new(10, .5));
        Assert.Equal(3, above.PasteUnits);
        Assert.True(above.DurationTicks > quote.DurationTicks);
        Assert.Equal(1, NaniteRepairRules.Quote(condition with { Damage = 1e-8 }, 1, 6500, new(10, .5)).DurationTicks);
    }

    [Fact]
    public void FreeAndHealthyQuotesDoNotInventPasteOrDelayAndCostBonusesDoNotChangeRepairTime()
    {
        ModuleCondition condition = new(1, 40, 10);
        Assert.Equal(new(condition, 0, 0), NaniteRepairRules.Quote(condition, 0, 6500, new(10, .5)));
        var healthy = condition with { Damage = 0 };
        Assert.Equal(new(healthy, 0, 0), NaniteRepairRules.Quote(healthy, 104000, 6500, new(10, .5)));
        var zeroCost = NaniteRepairRules.Quote(condition, 104000, 6500, new(10, 0));
        Assert.Equal(0, zeroCost.PasteUnits);
        Assert.Equal(TimeSpan.TicksPerMinute, zeroCost.DurationTicks);
        Assert.Equal(TimeSpan.TicksPerMinute / 2, NaniteRepairRules.Quote(condition, 104000, 6500, new(20, .5)).DurationTicks);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidPricesAndRatesAreRejected(double value)
    {
        ModuleCondition condition = new(1, 40, 10);
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.ComputedBasePrice(value, 1, []));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.ComputedBasePrice(1, 1, [new(value, 1, 1)]));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(condition, 1, value, new(10, .5)));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(condition, 1, 6500, new(value, .5)));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(condition, 1, 6500, new(10, value)));
    }

    [Fact]
    public void BurnoutMalformedPortionsAndNumericOverflowCannotProduceRepairQuotes()
    {
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(new(1, 40, 40), 1, 6500, new(10, .5)));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(new(1, 40, 45), 1, 6500, new(10, .5)));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.ComputedBasePrice(1, -1, []));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.ComputedBasePrice(1, 1, [new(1, -1, 1)]));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.ComputedBasePrice(1, 1, [new(1, 1, -1)]));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(new(1, 40, 1), -1, 6500, new(10, .5)));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(new(1, 40, 1), 1, 0, new(10, .5)));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Quote(new(1, 40, 1), 1, 6500, new(0, .5)));
        Assert.Throws<OverflowException>(() => NaniteRepairRules.ComputedBasePrice(double.MaxValue, 1, []));
        Assert.Throws<OverflowException>(() => NaniteRepairRules.Quote(new(1, 40, 10), 1, double.Epsilon, new(10, .5)));
        Assert.Throws<OverflowException>(() => NaniteRepairRules.Quote(new(1, 40, 10), 1, 6500, new(double.Epsilon, .5)));
    }
}
