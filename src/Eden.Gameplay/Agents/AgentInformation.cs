namespace Eden.Gameplay.Agents;

public sealed record AgentServiceInformation(CatalogAgent Agent, bool MissionService, bool MissionAvailable, string Incompatible);

public static class AgentInformationRules
{
    public static AgentServiceInformation Describe(
        CatalogAgent agent,
        AgentAccess access,
        bool hasContent,
        bool hasActiveMission,
        bool hasActiveStoryline
    )
    {
        if (agent.StationId is null)
        {
            return new(agent, false, false, "This agent has no supported station mission service.");
        }
        if (agent.Type is AgentType.GenericStoryline or AgentType.Storyline)
        {
            return new(agent, true, false, hasActiveStoryline ? "" : "This agent has no active storyline mission for you.");
        }
        if (agent.Type != AgentType.Basic)
        {
            return new(agent, false, false, "This special agent's mission service is not implemented.");
        }
        if (!access.Allowed)
        {
            return new(agent, true, false, access.Reason);
        }
        if (agent.DivisionId is not (MissionRules.SecurityDivision or MissionRules.DistributionDivision))
        {
            return new(agent, false, false, "This agent's mission division is not implemented.");
        }
        if (!hasContent && !hasActiveMission)
        {
            return new(agent, true, false, "No supported mission content is available for this agent and level yet.");
        }
        return new(agent, true, !hasActiveMission, "");
    }
}
