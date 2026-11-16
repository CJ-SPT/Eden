using Eden.Gameplay.Agents;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Flight;

namespace Eden.Protocol.Tests.Agents;

public sealed class FixedAgentRuleTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void AssignmentsRejectMissingOwnershipBindingAndPriorClosure(bool resident, bool assigned, bool closed) =>
        Assert.Throws<InvalidDataException>(() => FixedAgentRules.RequireAvailable(resident, assigned, closed));

    [Fact]
    public void AvailableAssignmentAndExactSurfaceRangeAreAccepted()
    {
        FixedAgentRules.RequireAvailable(true, true, false);
        AgentSpaceLocation agent = new(42, 1, SpacePosition.Zero, 100);
        Assert.True(FixedAgentRules.InRange(agent, 42, new(2600, 0, 0)));
        Assert.False(FixedAgentRules.InRange(agent, 42, new(2600.001, 0, 0)));
        Assert.False(FixedAgentRules.InRange(agent, 43, SpacePosition.Zero));
        Assert.False(FixedAgentRules.InRange(agent, 42, null));
    }

    [Theory]
    [InlineData(1, -0.01, false)]
    [InlineData(1, 0, true)]
    [InlineData(2, 0.99, false)]
    [InlineData(2, 1, true)]
    [InlineData(3, 2.99, false)]
    [InlineData(3, 3, true)]
    [InlineData(4, 4.99, false)]
    [InlineData(4, 5, true)]
    public void DataCenterStandingThresholdsIncludeTheLevelOneZeroBoundary(int level, double standing, bool allowed) =>
        Assert.Equal(allowed, FixedAgentRules.Access(level, standing, standing, standing, true).Allowed);

    [Theory]
    [InlineData(NpcHullClass.Frigate, 0)]
    [InlineData(NpcHullClass.Destroyer, 2)]
    [InlineData(NpcHullClass.Cruiser, 4)]
    [InlineData(NpcHullClass.Battlecruiser, 6)]
    [InlineData(NpcHullClass.Battleship, 8)]
    public void EachPirateHullCanDropBothGradesWithDeterministicProbabilityBoundaries(NpcHullClass hull, int first)
    {
        Assert.Equal(first, NpcTagLootRules.Grade(hull, 0));
        Assert.Equal(first + 1, NpcTagLootRules.Grade(hull, 4));
        foreach (long seed in new long[] { 1, 2, 3, 5, 6, 7 })
        {
            Assert.Null(NpcTagLootRules.Grade(hull, seed));
        }
        Assert.Equal(first, NpcTagLootRules.Grade(hull, 8));
    }
}
