using Eden.Gameplay.Agents;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Agents;

[Injectable(InjectionType.Scoped)]
internal sealed class MissionTrackerHandler(INpcDirectory directory, IPilotMissions missions) : ISelectedPilotCallHandler
{
    private long? _pilot;
    private readonly Dictionary<long, MissionTrackerObjective> _known = [];
    public bool IsMonitoring => _known.Count > 0;

    public void Reset()
    {
        _pilot = null;
        _known.Clear();
    }

    public void Advance(PilotCallContext context)
    {
        if (!context.Selected)
        {
            Reset();
            return;
        }
        if (_pilot != context.Pilot.PilotId)
        {
            _known.Clear();
            _pilot = context.Pilot.PilotId;
        }
        var entries = missions.Tracking(context.Pilot.PilotId);
        List<MissionTrackerUpdate> updates = [];
        HashSet<long> current = [];
        foreach (var entry in entries)
        {
            var objective = State(context, entry).Current;
            current.Add(entry.Offer.Agent.Id);
            if (!_known.TryGetValue(entry.Offer.Agent.Id, out var previous) || previous != objective)
            {
                updates.Add(new(entry.Offer.Agent.Id, objective));
                _known[entry.Offer.Agent.Id] = objective;
            }
        }
        foreach (long agent in _known.Keys.Where(agent => !current.Contains(agent)).ToArray())
        {
            updates.Add(new(agent, null));
            _known.Remove(agent);
        }
        if (updates.Count > 0)
        {
            context.Notifications.Add(new MissionsUpdated(updates));
        }
    }

    private static MissionTrackingState State(PilotCallContext context, MissionTrackingEntry entry) =>
        MissionTrackingRules.Read(
            entry,
            context.Pilot.SystemId,
            context.Pilot.Position is null ? context.Pilot.DockedLocationId : null,
            context.Flight.InWarp ? null : context.Pilot.Position
        );

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
        if (method == "UpdateAllMissions")
        {
            if (args is not [List<object?> agents] || agents.Any(id => id is not long agent || directory.Agent(agent) is null))
            {
                return new PilotCallResult.Failure("Refresh mission tracking with a list of valid agent IDs.");
            }
            if (_pilot != context.Pilot.PilotId)
            {
                _known.Clear();
                _pilot = context.Pilot.PilotId;
            }
            var entries = missions.Tracking(context.Pilot.PilotId).ToDictionary(entry => entry.Offer.Agent.Id);
            foreach (long agent in agents.Cast<long>().Distinct())
            {
                if (entries.TryGetValue(agent, out var entry))
                {
                    _known[agent] = State(context, entry).Current;
                }
                else
                {
                    _known.Remove(agent);
                }
            }
            context.Notifications.Add(
                new MissionsUpdated(
                    agents
                        .Cast<long>()
                        .Distinct()
                        .Select(agent => new MissionTrackerUpdate(
                            agent,
                            entries.TryGetValue(agent, out var entry) ? State(context, entry).Current : null
                        ))
                        .ToArray()
                )
            );
            return PilotCallResult.None;
        }
        if (args is not [long id] || directory.Agent(id) is null)
        {
            return new PilotCallResult.Failure("Choose a valid mission tracker agent.");
        }
        var selected = missions.Tracking(context.Pilot.PilotId).SingleOrDefault(entry => entry.Offer.Agent.Id == id);
        var state = selected is null ? null : State(context, selected);
        return method switch
        {
            "GetAllMissionObjectives" => new BlueList<MissionTrackerObjectiveReply>(
                state?.Objectives.Select(objective => new MissionTrackerObjectiveReply(objective)).ToArray() ?? []
            ),
            "IsInActiveDungeonID" => state?.InDungeon == true,
            _ => state is null ? PilotCallResult.None : new MissionTrackerObjectiveReply(state.Current, true),
        };
    }
}
