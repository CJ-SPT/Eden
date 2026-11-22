using Eden.Gameplay.Capsuleers;

namespace Eden.Protocol.Tests.Capsuleers;

public sealed class CapsuleerWorkRulesTests
{
    [Fact]
    public void TheSamePilotRestocksThenHuntsUntilAReplacementShortageReturns()
    {
        const bool canHunt = true;

        Assert.Equal(CapsuleerWorkKind.Industry, CapsuleerWorkRules.Choose(industryDemand: true, canHunt, readyReplacements: 1));
        Assert.Equal(CapsuleerWorkKind.Hunt, CapsuleerWorkRules.Choose(industryDemand: false, canHunt, readyReplacements: 2));
        Assert.Equal(CapsuleerWorkKind.Industry, CapsuleerWorkRules.Choose(industryDemand: true, canHunt, readyReplacements: 1));
    }

    [Fact]
    public void UnresolvedIndustryDemandTakesPriorityEvenWithEnoughHuntingReplacements()
    {
        Assert.Equal(CapsuleerWorkKind.Industry, CapsuleerWorkRules.Choose(industryDemand: true, canHunt: true, readyReplacements: 3));
    }

    [Theory]
    [InlineData(true, -1, CapsuleerWorkKind.Routine)]
    [InlineData(true, 0, CapsuleerWorkKind.Routine)]
    [InlineData(true, 1, CapsuleerWorkKind.Routine)]
    [InlineData(true, 2, CapsuleerWorkKind.Hunt)]
    [InlineData(false, 3, CapsuleerWorkKind.Routine)]
    public void HuntingRequiresAnEligiblePilotAndTwoReadyReplacements(bool canHunt, int replacements, CapsuleerWorkKind expected)
    {
        Assert.Equal(expected, CapsuleerWorkRules.Choose(industryDemand: false, canHunt, replacements));
    }
}
