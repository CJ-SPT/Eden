using Eden.Gameplay.Agents;

namespace Eden.Protocol.Tests.Agents;

public sealed class AgentInformationRulesTests
{
    private static readonly CatalogAgent Agent = new(
        MissionFixture.AgentId,
        MissionFixture.StationId,
        30000142,
        1000044,
        AgentType.Basic,
        500001,
        MissionRules.DistributionDivision,
        1,
        true
    );
    private static readonly AgentAccess Allowed = new(true, "", 0, 0, 0, -10);

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void OrdinaryMissionAvailabilityRequiresAnIdleAgentWithSupportedContent(bool active, bool available)
    {
        var result = AgentInformationRules.Describe(Agent, Allowed, true, active, false);
        Assert.True(result.MissionService);
        Assert.Equal(available, result.MissionAvailable);
        Assert.Empty(result.Incompatible);
    }

    [Fact]
    public void DeniedStandingLocationDivisionAndContentDoNotAdvertiseAvailableMissions()
    {
        var denied = AgentInformationRules.Describe(Agent, new(false, "Effective standing is too low.", -3, 0, 0, 1), true, false, false);
        Assert.False(denied.MissionAvailable);
        Assert.Equal("Effective standing is too low.", denied.Incompatible);
        foreach (var agent in new[] { Agent with { StationId = null }, Agent with { DivisionId = 18 } })
        {
            var result = AgentInformationRules.Describe(agent, Allowed, true, false, false);
            Assert.False(result.MissionService);
            Assert.False(result.MissionAvailable);
            Assert.NotEmpty(result.Incompatible);
        }
        var missing = AgentInformationRules.Describe(Agent, Allowed, false, false, false);
        Assert.False(missing.MissionAvailable);
        Assert.Contains("content", missing.Incompatible);
    }

    [Theory]
    [InlineData(AgentType.Research)]
    [InlineData(AgentType.Career)]
    [InlineData(AgentType.EpicArc)]
    [InlineData(AgentType.FactionalWarfare)]
    [InlineData(AgentType.Event)]
    public void SpecialAgentsRemainInspectableWithoutAdvertisingUnimplementedServices(AgentType type)
    {
        var result = AgentInformationRules.Describe(Agent with { Type = type }, Allowed, true, false, false);
        Assert.False(result.MissionService);
        Assert.False(result.MissionAvailable);
        Assert.NotEmpty(result.Incompatible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StorylinesDoNotOfferRepeatMissionsAndOnlyExistingOffersClearTheCompatibilityMessage(bool active)
    {
        var result = AgentInformationRules.Describe(Agent with { Type = AgentType.GenericStoryline }, Allowed, false, active, active);
        Assert.True(result.MissionService);
        Assert.False(result.MissionAvailable);
        Assert.Equal(active, result.Incompatible.Length == 0);
    }
}
