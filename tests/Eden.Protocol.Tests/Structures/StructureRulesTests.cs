using Eden.Gameplay.Structures;

namespace Eden.Protocol.Tests.Structures;

public sealed class StructureRulesTests
{
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
