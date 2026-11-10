using Eden.Gameplay.Ships;
using Eden.Gameplay.Structures;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureRulesTests
{
    [Theory]
    [InlineData(StructureFamily.Citadel, StructureSize.Medium, HullClass.Freighter, true)]
    [InlineData(StructureFamily.Citadel, StructureSize.Medium, HullClass.Carrier, false)]
    [InlineData(StructureFamily.Citadel, StructureSize.Large, HullClass.Carrier, true)]
    [InlineData(StructureFamily.Citadel, StructureSize.Large, HullClass.Titan, false)]
    [InlineData(StructureFamily.Citadel, StructureSize.ExtraLarge, HullClass.Titan, true)]
    [InlineData(StructureFamily.EngineeringComplex, StructureSize.Large, HullClass.Carrier, false)]
    [InlineData(StructureFamily.EngineeringComplex, StructureSize.ExtraLarge, HullClass.Carrier, true)]
    [InlineData(StructureFamily.EngineeringComplex, StructureSize.ExtraLarge, HullClass.Supercarrier, false)]
    [InlineData(StructureFamily.Refinery, StructureSize.Medium, HullClass.CapitalIndustrial, false)]
    [InlineData(StructureFamily.Refinery, StructureSize.Large, HullClass.CapitalIndustrial, true)]
    [InlineData(StructureFamily.Refinery, StructureSize.Large, HullClass.CommandCarrier, false)]
    [InlineData(StructureFamily.JumpBridge, StructureSize.Flex, HullClass.Frigate, false)]
    public void DockingUsesHullFamilyAndSizeRatherThanStructureSizeAlone(
        StructureFamily family,
        StructureSize size,
        HullClass hull,
        bool expected
    )
    {
        StructureProfile profile = new(1, "Structure", 1, family, size, 1, null, null, 3, 1, 1, 1, 1, 1, 1, false);
        Assert.Equal(expected, StructureRules.CanDock(profile, StructureState.ShieldVulnerable, (int)hull));
        Assert.False(StructureRules.CanDock(profile, StructureState.Anchoring, (int)hull));
        Assert.False(StructureRules.CanDock(profile, StructureState.Unanchored, (int)hull));
    }

    [Theory]
    [InlineData(StructureFamily.EngineeringComplex, StructureService.BasicManufacturing, 9)]
    [InlineData(StructureFamily.EngineeringComplex, StructureService.Reprocessing, 12)]
    [InlineData(StructureFamily.Refinery, StructureService.Reprocessing, 9)]
    [InlineData(StructureFamily.Citadel, StructureService.BasicManufacturing, 12)]
    public void FuelRoleBonusAppliesOnlyToMatchingServicesAndRoundsUp(StructureFamily family, StructureService service, int expected)
    {
        Assert.Equal(expected, StructureRules.FuelAmount(family, [service], -25, 12));
        Assert.Equal(1, StructureRules.FuelAmount(family, [service], -25, 1));
    }

    [Fact]
    public void StructureSpeedChangesDurationWithoutAllowingZeroOrUnboundedRates()
    {
        Assert.Equal(TimeSpan.TicksPerHour * 12, StructureRules.Duration(StructureRules.AnchoringDuration, 2));
        Assert.Equal(TimeSpan.TicksPerMinute * 3, StructureRules.Duration(StructureRules.RepairDuration, 5));
        Assert.Equal(TimeSpan.TicksPerDay * 70, StructureRules.Duration(StructureRules.UnanchoringDuration, .1m));
        Assert.Equal(1, StructureRules.Duration(1, 100));
        Assert.Throws<InvalidDataException>(() => StructureRules.Duration(StructureRules.AnchoringDuration, 0));
        Assert.Throws<InvalidDataException>(() => StructureRules.Duration(StructureRules.AnchoringDuration, 101));
    }
}
