using Eden.Gameplay.Characters;
using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

[Injectable]
internal sealed class TaskTrackingHandler(IPilotTaskTracking tracking) : ISelectedPilotCallHandler
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
            service != "monolithTaskTrackerMgr"
            || method is not ("HasEverWarped" or "GetCompletedAchievementsAndClientEventCount" or "UpdateClientAchievmentsAndCounters")
        )
        {
            return PilotCallResult.Unsupported;
        }
        try
        {
            if (keywords is { Count: > 0 })
            {
                throw new InvalidDataException("Unexpected task tracking arguments.");
            }
            switch (method, args)
            {
                case ("HasEverWarped", []):
                    return tracking.Read(context.Pilot.PilotId).HasWarped;
                case ("GetCompletedAchievementsAndClientEventCount", []):
                    return new TaskProgressReply(tracking.Read(context.Pilot.PilotId));
                case ("UpdateClientAchievmentsAndCounters", [var tasks, var counters]):
                    var completed = Entries(tasks)
                        .Select(entry =>
                            entry.Key is long id && entry.Value is long time
                                ? new CompletedTask(id, time)
                                : throw new InvalidDataException("Invalid task completion.")
                        )
                        .ToArray();
                    var counts = Entries(counters)
                        .Select(entry =>
                            entry.Key is string name && entry.Value is long count
                                ? new TaskCounter(name, count)
                                : throw new InvalidDataException("Invalid task counter.")
                        )
                        .ToArray();
                    tracking.Update(context.Pilot.PilotId, completed, counts);
                    return PilotCallResult.None;
                default:
                    throw new InvalidDataException("Invalid task tracking request.");
            }
        }
        catch (InvalidDataException error)
        {
            return new PilotCallResult.Failure(error.Message);
        }
    }

    private static IEnumerable<KeyValuePair<object, object?>> Entries(object? value) =>
        value switch
        {
            Dictionary<object, object?> entries when entries.Count <= TaskTrackingRules.MaximumBatch => entries,
            Dictionary<long, object?> entries when entries.Count <= TaskTrackingRules.MaximumBatch => entries.Select(
                entry => new KeyValuePair<object, object?>(entry.Key, entry.Value)
            ),
            Dictionary<string, object?> entries when entries.Count <= TaskTrackingRules.MaximumBatch => entries.Select(
                entry => new KeyValuePair<object, object?>(entry.Key, entry.Value)
            ),
            _ => throw new InvalidDataException("Expected a bounded task dictionary."),
        };
}

internal sealed record TaskProgressReply(TaskProgress Progress) : BlueModel
{
    public override object ToBlue() =>
        new Dictionary<string, object?>
        {
            ["completedDict"] = Progress.Completed.ToDictionary(task => task.Id, task => (object?)task.Time),
            ["eventDict"] = Progress.ClientCounters.ToDictionary(counter => counter.Name, counter => (object?)counter.Count),
        };
}
