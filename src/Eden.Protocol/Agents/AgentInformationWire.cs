using Eden.Gameplay.Agents;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

internal sealed record AgentServiceDetailsReply(AgentServiceInformation Information) : BlueModel
{
    // Pinned Info.UpdateDataAgent reads these attributes; ProcessAgentInfoKeyVal
    // dispatches the service using the literal string "mission", not a numeric ID.
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["agentID"] = Information.Agent.Id,
                ["stationID"] = Information.Agent.StationId,
                ["level"] = Information.Agent.Level,
                ["services"] = new BlueList<AgentMissionServiceReply>(
                    Information.MissionService ? [new AgentMissionServiceReply(Information.MissionAvailable)] : []
                ),
                ["incompatible"] = Information.Incompatible.Length == 0 ? null : new BlueUnicode(Information.Incompatible),
            }
        );
}

internal sealed record AgentMissionServiceReply(bool Available) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance("utillib.KeyVal", new Dictionary<string, object?> { ["agentServiceType"] = "mission", ["available"] = Available });
}

internal sealed record AgentStaticInfoReply(CatalogAgent Agent) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["agentID"] = Agent.Id,
                ["stationID"] = Agent.StationId,
                ["corporationID"] = Agent.CorporationId,
                ["solarsystemID"] = Agent.SystemId,
                ["solarSystemID"] = Agent.SystemId,
                ["agentTypeID"] = (int)Agent.Type,
                ["factionID"] = Agent.FactionId,
                ["divisionID"] = Agent.DivisionId,
                ["level"] = Agent.Level,
                ["quality"] = AgentDirectoryRow.RetiredAgentQuality,
                ["isLocator"] = Agent.Locator,
            }
        );
}
