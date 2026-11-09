using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable]
internal sealed class MissionTrackerHandler(INpcDirectory directory) : ISelectedPilotCallHandler
{
    public PilotCallResult Call(
        PilotCallContext context,
        string service,
        string method,
        object?[] args,
        Dictionary<string, object?>? keywords = null
    )
    {
        if (
            service != "missionTrackerMgr"
            || method is not ("UpdateAllMissions" or "GetMissionInfoItems" or "GetAllMissionObjectives" or "IsInActiveDungeonID")
        )
        {
            return PilotCallResult.Unsupported;
        }
        if (keywords is { Count: > 0 })
        {
            return new PilotCallResult.Failure("Unexpected mission tracker keyword arguments.");
        }
        // Build 3662168 MissionObjectivesTracker.UpdateMissionStatusData sends one
        // Python list of agent IDs; the other three calls send one agent ID.
        if (method == "UpdateAllMissions")
        {
            return args is [List<object?> agents] && agents.All(id => id is long agentId && directory.Agent(agentId) is not null)
                ? PilotCallResult.None
                : new PilotCallResult.Failure("Refresh mission tracking with a list of valid agent IDs.");
        }
        if (args is not [long id] || directory.Agent(id) is null)
        {
            return new PilotCallResult.Failure("Choose a valid mission tracker agent.");
        }
        // Eden's mission sites have no legacy dungeon-tracker registrations. The
        // current mission and progress remain available through the native journal
        // and GetMissionObjectiveInfo; do not invent legacy objective/event tokens.
        return method switch
        {
            "GetAllMissionObjectives" => BlueEmpty.List,
            "IsInActiveDungeonID" => false,
            _ => PilotCallResult.None,
        };
    }
}
