using System.Globalization;
using Eden.Gameplay.Agents;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

internal sealed record MissionTrackerObjectiveReply(MissionTrackerObjective Objective, bool TextArguments = false) : BlueModel
{
    public override object ToBlue()
    {
        object?[] values = Objective.Kind switch
        {
            MissionObjectiveKind.Travel => ["TravelTo", Objective.LocationId],
            MissionObjectiveKind.TalkToAgent => ["TalkToAgent", Objective.TargetId],
            MissionObjectiveKind.TransportMissing => ["TransportItemsMissing", Objective.TypeId, Objective.Quantity],
            MissionObjectiveKind.TransportPresent => ["TransportItemsPresent", Objective.TypeId, Objective.LocationId, Objective.Quantity],
            MissionObjectiveKind.DestroyAll => ["DestroyAll"],
            MissionObjectiveKind.Destroy => ["Destroy", Objective.TypeId, Objective.TargetId, null],
            MissionObjectiveKind.Fetch => ["MissionFetch", Objective.TypeId, Objective.Quantity],
            MissionObjectiveKind.Mine =>
            [
                TextArguments ? "MissionFetchMineTrigger" : "MissionFetchMine",
                Objective.TypeId,
                Objective.Quantity,
            ],
            MissionObjectiveKind.ReturnMinedCargo =>
            [
                "FetchObjectAcquiredDungeonDone",
                Objective.TypeId,
                Objective.AgentId,
                Objective.LocationId,
                Objective.Quantity,
            ],
            MissionObjectiveKind.Complete => ["AllObjectivesComplete", Objective.AgentId],
            _ => throw new InvalidDataException("Unknown mission objective."),
        };
        if (!TextArguments)
        {
            return values;
        }
        // The TalkToAgent objective forwards its target directly to the agent service without parsing.
        return Objective.Kind == MissionObjectiveKind.TalkToAgent
            ? values.ToList()
            : values.Select(value => value is IFormattable number ? number.ToString(null, CultureInfo.InvariantCulture) : value).ToList();
    }
}

internal sealed record MissionTrackerUpdate(long AgentId, MissionTrackerObjective? Objective) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["agentID"] = AgentId,
            ["info"] = Objective is null ? null : new MissionTrackerObjectiveReply(Objective, true),
        };
}

internal sealed record MissionsUpdated(IReadOnlyList<MissionTrackerUpdate> Updates) : PilotNotification("OnMissionsUpdated")
{
    public override object?[] Arguments => [new BlueList<MissionTrackerUpdate>(Updates)];
}
