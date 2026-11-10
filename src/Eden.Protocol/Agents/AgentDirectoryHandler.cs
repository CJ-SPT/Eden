using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable]
internal sealed class AgentDirectoryHandler(INpcDirectory directory) : ISelectedPilotCallHandler
{
    private const int MaximumCareerAgentIds = 1000;

    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "agentMgr"
            || method
                is not (
                    "GetAgents"
                    or "GetAgentStaticInfo"
                    or "GetAgentByID"
                    or "GetSolarSystemOfAgent"
                    or "GetCompletedCareerAgentIDs"
                    or "GetMyEpicArcStatus"
                    or "GetMessagesForEpicArcMissions"
                )
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected agent directory keywords.");
        }
        switch (method, args)
        {
            case ("GetAgents", []):
                return new AgentDirectoryReply(directory.Agents());
            case ("GetMyEpicArcStatus" or "GetMessagesForEpicArcMissions", []):
                // Eden has no authored epic arcs or saved epic-arc progression.
                // Both native consumers use dictionary lookups, not list iteration.
                return BlueEmpty.IndexedDictionary;
            case ("GetAgentStaticInfo" or "GetAgentByID" or "GetSolarSystemOfAgent", [long id]) when id > 0:
                if (directory.Agent(id) is not { } agent)
                {
                    return new PilotCallResult.Failure("Choose an active NPC agent.");
                }
                if (method == "GetSolarSystemOfAgent")
                {
                    return agent.SystemId is int system ? system : PilotCallResult.None;
                }
                return new AgentStaticInfoReply(agent);
            case ("GetCompletedCareerAgentIDs", [List<object?> ids]) when ids.Count <= MaximumCareerAgentIds:
                if (ids.Any(value => value is not long id || !NpcCharacterIds.Contains(id)))
                {
                    return new PilotCallResult.Failure("Choose valid NPC agent IDs.");
                }
                var agents = ids.Cast<long>().Distinct().ToArray();
                if (agents.Any(id => directory.Agent(id) is null))
                {
                    return new PilotCallResult.Failure("Choose active NPC agents.");
                }
                // Career mission chains are not implemented; every requested
                // agent must still be present so the native cache can prime once.
                return new BlueMap<long, bool>(agents.ToDictionary(id => id, _ => false));
            default:
                return new PilotCallResult.Failure("Invalid agent directory request.");
        }
    }
}

internal sealed record AgentDirectoryReply(IReadOnlyList<CatalogAgent> Agents) : BlueModel
{
    public override object ToBlue() =>
        new CompatibilityRowset(
            [
                "agentID",
                "stationID",
                "corporationID",
                "solarsystemID",
                "agentTypeID",
                "factionID",
                "divisionID",
                "level",
                "quality",
                "isLocator",
            ],
            Agents.Select(agent => new AgentDirectoryRow(agent)).ToArray()
        ).ToBlue();
}

internal sealed record AgentDirectoryRow(CatalogAgent Agent) : BlueModel
{
    // Agent quality was removed in Incursion 1.5. This legacy display column
    // uses the uniform payout quality; it does not implement mission rewards/access.
    internal const int RetiredAgentQuality = 20;

    public override object ToBlue() =>
        new object?[]
        {
            Agent.Id,
            Agent.StationId,
            Agent.CorporationId,
            Agent.SystemId,
            (int)Agent.Type,
            Agent.FactionId,
            Agent.DivisionId,
            Agent.Level,
            RetiredAgentQuality,
            Agent.Locator,
        };
}
