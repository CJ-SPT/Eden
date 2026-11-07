using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Docking;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable]
internal sealed class AgentDirectoryHandler(INpcDirectory directory) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if ((service, method) != ("agentMgr", "GetAgents"))
        {
            return PilotCallResult.Unsupported;
        }
        return args.Length == 0 && keywords is null or { Count: 0 }
            ? new AgentDirectoryReply(directory.Agents())
            : new PilotCallResult.Failure("Request the agent directory without arguments.");
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
    private const int RetiredAgentQuality = 20;

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
