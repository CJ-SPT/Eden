using Eden.Gameplay.Agents;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

internal sealed record AgentServiceDetailsReply(AgentServiceInformation Information) : BlueModel
{
    // Pinned Info.UpdateDataAgent reads these attributes; ProcessAgentInfoKeyVal
    // dispatches services using literal names such as "mission" and "research".
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["agentID"] = Information.Agent.Id,
                ["stationID"] = Information.Agent.StationId,
                ["level"] = Information.Agent.Level,
                ["services"] = new BlueList<BlueModel>(
                    Information.Agent is { Type: AgentType.Research, StationId: not null } ? [new AgentResearchServiceReply(Information)]
                    : Information.MissionService ? [new AgentMissionServiceReply(Information.MissionAvailable)]
                    : []
                ),
                ["incompatible"] = Information.Incompatible.Length == 0 ? null : new BlueUnicode(Information.Incompatible),
            }
        );
}

internal sealed record AgentResearchServiceReply(AgentServiceInformation Information) : BlueModel
{
    public override object ToBlue() =>
        new BlueInstance(
            "utillib.KeyVal",
            new Dictionary<string, object?>
            {
                ["agentServiceType"] = "research",
                ["skills"] = Information
                    .ResearchFields.Select(f =>
                        (object?)
                            new object?[]
                            {
                                f.SkillId,
                                new BlueInstance(
                                    "utillib.KeyVal",
                                    new Dictionary<string, object?> { ["effectiveSkillLevel"] = Information.Agent.Level }
                                ),
                            }
                    )
                    .ToList(),
                ["researchData"] = Information.Research is not { } project
                    ? null
                    : new Dictionary<string, object?>
                    {
                        ["skillTypeID"] = project.SkillId,
                        ["points"] = project.Points,
                        ["pointsPerDay"] = project.PointsPerDay,
                    },
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
                ["bloodlineID"] = Agent.BloodlineId,
            }
        );
}
