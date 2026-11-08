using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class NaniteRepairRuleTests
{
    private static readonly Guid Generation = Guid.Parse("4906e4f7-727d-4ad1-aa62-83b14afafce7");

    private static NaniteRepairSchedule? Start(NaniteRepairQuote quote, long pilot, long ship, long now) =>
        NaniteRepairRules.Start(quote, pilot, ship, now, Generation);

    [Fact]
    public void PaidSchedulePreservesFractionalTicksStopsAtItsDeadlineAndDoesNotAdvanceOnClockRewind()
    {
        ModuleCondition condition = new(1, 40, 20);
        var quote = NaniteRepairRules.Quote(condition, 104000, 6500, new(10, .5));
        var schedule = Assert.IsType<NaniteRepairSchedule>(Start(quote, 2, 3, 1000));
        Assert.Equal(new(1, 1000, 1000 + 2 * TimeSpan.TicksPerMinute, Generation), schedule.Key);
        Assert.Equal(40, schedule.InitialHitPoints);
        Assert.Equal(20, schedule.InitialDamage);
        Assert.Equal(0, NaniteRepairRules.Repaired(schedule, 999));
        Assert.Equal(0, NaniteRepairRules.Repaired(schedule, 1000));
        Assert.Equal(20.0 / (2 * TimeSpan.TicksPerMinute), NaniteRepairRules.Repaired(schedule, 1001), 14);
        Assert.Equal(10, NaniteRepairRules.Repaired(schedule, 1000 + TimeSpan.TicksPerMinute));
        Assert.Equal(20, NaniteRepairRules.Repaired(schedule, schedule.Key.CompletesAt));
        Assert.Equal(20, NaniteRepairRules.Repaired(schedule, long.MaxValue));
        Assert.Equal(20, condition.Damage);
    }

    [Fact]
    public void PartialRefundChargesEarnedDamageOnceAndRetainsLegacyPrepaidReservations()
    {
        var quote = NaniteRepairRules.Quote(new(1, 40, 20), 2600000, 6500, new(10, .5));
        var schedule = Start(quote, 2, 3, 1000)!;
        Assert.Equal(100, schedule.PasteUnits);
        Assert.Equal(100, NaniteRepairRules.Refund(schedule, 1000));
        Assert.Equal(99, NaniteRepairRules.Refund(schedule, 1001));
        Assert.Equal(75, NaniteRepairRules.Refund(schedule, 1000 + TimeSpan.TicksPerMinute / 2));
        Assert.Equal(0, NaniteRepairRules.Refund(schedule, schedule.Key.CompletesAt));
        Assert.Equal(0, NaniteRepairRules.Refund(schedule with { PasteUnitsPerDamage = 0 }, 1000));
    }

    [Fact]
    public void InvalidAndOverflowingSchedulesCannotBecomePaidRepairs()
    {
        ModuleCondition condition = new(1, 40, 20);
        NaniteRepairQuote quote = new(condition, 1, 10);
        Assert.Throws<OverflowException>(() => Start(quote, 2, 3, long.MaxValue));
        Assert.Throws<InvalidDataException>(() => Start(quote, 0, 3, 0));
        Assert.Throws<InvalidDataException>(() => Start(quote, 2, 0, 0));
        Assert.Throws<InvalidDataException>(() => Start(quote, 2, 3, -1));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Start(quote, 2, 3, 0, Guid.Empty));
        Assert.Throws<InvalidDataException>(() => Start(quote with { DurationTicks = 0 }, 2, 3, 0));
        Assert.Throws<InvalidDataException>(() => Start(quote with { PasteUnits = -1 }, 2, 3, 0));
        Assert.Throws<InvalidDataException>(() => Start(quote with { Condition = condition with { Damage = 40 } }, 2, 3, 0));
        var schedule = Start(quote, 2, 3, 0)!;
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Validate(schedule with { InitialHitPoints = 20 }));
        Assert.Throws<InvalidDataException>(() =>
            NaniteRepairRules.Validate(schedule with { Key = schedule.Key with { Generation = Guid.Empty } })
        );
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Validate(schedule with { InitialDamage = double.PositiveInfinity }));
        Assert.Throws<InvalidDataException>(() => NaniteRepairRules.Repaired(schedule, -1));
        Assert.Null(Start(new(condition, 0, 0), 2, 3, 0));
        Assert.Equal(
            1,
            NaniteRepairRules.Quote(condition with { Damage = double.Epsilon }, long.MaxValue, 6500, new(double.MaxValue, .5)).DurationTicks
        );
    }

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
