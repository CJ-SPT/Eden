using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Thermal;

namespace Eden.Protocol.Tests.Thermal;

public sealed class HeatRulesTests
{
    public static IEnumerable<object[]> NativeVectors() =>
        ThermalFixtures
            .Read<ThermalFixtures.Reference>("HeatReferenceVectors.json")
            .Vectors.Select(v => new object[] { v.Heat, v.Milliseconds, v.Incoming, v.Dissipation, v.Generation, v.Capacity, v.Expected });

    [Theory]
    [MemberData(nameof(NativeVectors))]
    public void HeatingAndCoolingMatchInertPinnedNativeFunctionVectors(
        double heat,
        double milliseconds,
        double incoming,
        double dissipation,
        double generation,
        double capacity,
        double expected
    )
    {
        HeatRackProfile rack = new(ModuleRack.High, capacity, dissipation, generation, .25);
        Assert.Equal(expected, HeatRules.Calculate(rack, heat, incoming, milliseconds), 11);
    }

    [Fact]
    public void NativeReferenceRemainsBoundToThePinnedArchive()
    {
        var reference = ThermalFixtures.Read<ThermalFixtures.Reference>("HeatReferenceVectors.json");
        Assert.Equal(3662168, reference.Build);
        Assert.Equal("37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368", reference.ArchiveSha256);
        Assert.Equal(13, reference.Vectors.Count);
    }

    [Fact]
    public void PopulatedRacksAcrossEveryAdmittedHullUseTheirOwnThermalAttributes()
    {
        var matrix = ThermalFixtures.Read<ThermalFixtures.Matrix>("ThermalModuleMatrix.json");
        Assert.Equal(235, matrix.Hulls.Count);
        int racks = 0;
        foreach (var hull in matrix.Hulls)
        {
            foreach (var rack in new[] { ModuleRack.Low, ModuleRack.Medium, ModuleRack.High })
            {
                var slot = rack switch
                {
                    ModuleRack.Low => DogmaAttributeId.LowSlots,
                    ModuleRack.Medium => DogmaAttributeId.MediumSlots,
                    _ => DogmaAttributeId.HighSlots,
                };
                if (hull.Slots[slot] == 0)
                {
                    continue;
                }
                var profile = HeatRules.Define(rack, hull.Attributes);
                Assert.Equal(100, profile.Capacity);
                Assert.Equal(.01, profile.Dissipation);
                Assert.InRange(profile.Attenuation, 0, 1);
                Assert.True(HeatRules.Calculate(profile, 0, .01, 1000) > 0);
                racks++;
            }
        }
        Assert.True(racks > 500);
    }

    [Fact]
    public void FractionalTicksAndClockRegressionPreserveTheAuthoritativeTimeOrigin()
    {
        TestClock clock = new();
        HeatRackProfile rack = new(ModuleRack.Medium, 100, .01, 1, .25);
        HeatRackState initial = new(20, clock.Now);
        clock.AdvanceWallTime(TimeSpan.FromTicks(1));
        var advanced = HeatRules.Advance(rack, initial, .01, clock.Now);
        Assert.True(advanced.Heat > initial.Heat);
        Assert.Equal(clock.Now, advanced.UpdatedAt);
        Assert.Equal(advanced, HeatRules.Advance(rack, advanced, .01, initial.UpdatedAt));
        Assert.Throws<InvalidDataException>(() => HeatRules.Advance(rack, initial, 0, -1));
    }

    [Theory]
    [InlineData(double.NaN, 0, 0)]
    [InlineData(-1, 0, 0)]
    [InlineData(0, double.PositiveInfinity, 0)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, double.NaN)]
    [InlineData(0, 0, -1)]
    public void InvalidHeatRatesAndElapsedTimesAreRejected(double heat, double incoming, double milliseconds)
    {
        HeatRackProfile rack = new(ModuleRack.High, 100, .01, 1, .25);
        Assert.Throws<InvalidDataException>(() => HeatRules.Calculate(rack, heat, incoming, milliseconds));
    }

    [Theory]
    [InlineData(0, .01, 1, .25)]
    [InlineData(100, -1, 1, .25)]
    [InlineData(100, .01, double.NaN, .25)]
    [InlineData(100, .01, 1, 1.01)]
    public void InvalidRackProfilesAreRejected(double capacity, double dissipation, double generation, double attenuation)
    {
        HeatRackProfile rack = new(ModuleRack.Low, capacity, dissipation, generation, attenuation);
        Assert.Throws<InvalidDataException>(() => HeatRules.Calculate(rack, 0, 0, 0));
    }

    [Fact]
    public void RigsAreExcludedAndRacksHeatAndCoolIndependently()
    {
        Assert.Throws<InvalidDataException>(() => HeatRules.Define(ModuleRack.Rig, new Dictionary<DogmaAttributeId, double>()));
        HeatRackProfile rack = new(ModuleRack.High, 100, .01, 1, .25);
        HeatRackState high = new(20, 100);
        HeatRackState medium = new(20, 100);
        Assert.True(HeatRules.Advance(rack, high, .02, 100 + TimeSpan.TicksPerSecond).Heat > 20);
        Assert.True(HeatRules.Advance(rack with { Rack = ModuleRack.Medium }, medium, 0, 100 + TimeSpan.TicksPerSecond).Heat < 20);
        Assert.Equal(20, high.Heat);
        Assert.Equal(20, medium.Heat);
        Assert.Throws<InvalidDataException>(() => HeatRules.Calculate(rack with { Generation = 0 }, 20, double.MaxValue, double.MaxValue));
    }
}
