using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable]
internal sealed class AgentDirectoryHandler(INpcDirectory directory, IPilotMissions missions) : ISelectedPilotCallHandler
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
        bool supported = (service, method) switch
        {
            ("epicArcStatus", "AgentHasEpicMissionsForCharacter") => true,
            (
                "agentMgr",
                "GetAgents"
                    or "GetAgentStaticInfo"
                    or "GetAgentByID"
                    or "GetSolarSystemOfAgent"
                    or "GetCompletedCareerAgentIDs"
                    or "GetMyEpicArcStatus"
                    or "GetMessagesForEpicArcMissions"
            ) => true,
            _ => false,
        };
        if (!supported)
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected agent directory keywords.");
        }
        if (service == "epicArcStatus")
        {
            return args is [long epicAgent] && directory.Agent(epicAgent) is not null
                ? missions.EpicArcAgentAvailable(context.Pilot.PilotId, epicAgent)
                : new PilotCallResult.Failure("Choose an active NPC agent.");
        }
        switch (method, args)
        {
            case ("GetAgents", []):
                return new AgentDirectoryReply(directory.Agents());
            case ("GetMyEpicArcStatus", []):
                return new EpicArcStatusReply(missions.ArcStatus(context.Pilot.PilotId));
            case ("GetMessagesForEpicArcMissions", []):
                var messages = missions.EpicArcMessages();
                return messages.Count == 0 ? BlueEmpty.IndexedDictionary : new CachedEpicArcMessages(messages);
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
                var agents = ids.Cast<long>().ToHashSet();
                if (agents.Any(id => directory.Agent(id) is null))
                {
                    return new PilotCallResult.Failure("Choose active NPC agents.");
                }
                var completed = missions.CompletedCareerAgents(context.Pilot.PilotId, agents);
                return new BlueMap<long, bool>(agents.ToDictionary(id => id, completed.Contains));
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
                "isLocatorAgent",
                "bloodlineID",
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
            Agent.Space is null ? Agent.StationId : null,
            Agent.CorporationId,
            Agent.SystemId,
            (int)Agent.Type,
            Agent.FactionId,
            Agent.DivisionId,
            Agent.Level,
            RetiredAgentQuality,
            Agent.Locator,
            // The local agents service caches this row unchanged. CharacterInfoWindow
            // reads bloodlineID before constructing the agent header or service tabs.
            Agent.BloodlineId,
        };
}
