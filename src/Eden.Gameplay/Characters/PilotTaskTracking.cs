namespace Eden.Gameplay.Characters;

public sealed record TaskCounter(string Name, long Count);

public sealed record CompletedTask(long Id, long Time);

public sealed record TaskCondition(string Event, long Target);

public sealed record TaskDefinition(long Id, bool Client, bool Enabled, IReadOnlyList<TaskCondition> Conditions);

public sealed record TaskProgress(IReadOnlyList<CompletedTask> Completed, IReadOnlyList<TaskCounter> ClientCounters, bool HasWarped);

public interface IPilotTaskTracking
{
    TaskProgress Read(long pilotId);
    void Update(long pilotId, IReadOnlyList<CompletedTask> completed, IReadOnlyList<TaskCounter> counters);
}

public static class TaskTrackingRules
{
    public const int MaximumBatch = 1024;
    public const long MaximumCounter = long.MaxValue;

    public static bool Satisfied(TaskDefinition task, IReadOnlyList<TaskCounter> counters) =>
        task.Enabled
        && task.Conditions.Count > 0
        && task.Conditions.All(condition => counters.FirstOrDefault(counter => counter.Name == condition.Event)?.Count >= condition.Target);

    public static long MergeCount(long saved, long incoming)
    {
        if (incoming < 0)
        {
            throw new InvalidDataException("Task counters cannot be negative.");
        }
        // The client sends its complete cumulative snapshot, including at shutdown.
        return Math.Max(saved, incoming);
    }

    public static long Increment(long saved, long amount) =>
        amount < 0 ? throw new InvalidDataException("Task increments cannot be negative.")
        : saved > MaximumCounter - amount ? MaximumCounter
        : saved + amount;
}

// Names from build 3662168 client/achievementevents; no numeric event IDs
// leak into gameplay operations. Unsupported systems do not earn these events.
public static class TaskEvents
{
    public const string Warped = "timesWarped";
    public const string Jumped = "stargateJumps";
    public const string Docked = "dockInStation";
    public const string Undocked = "undockFromStation";
    public const string Bought = "buyCount";
    public const string Sold = "sellCount";
    public const string MiningLaserActivated = "activateMiningLaser";
    public const string Mined = "timesMined";
    public const string TotalMined = "totalMined";
    public const string NpcDestroyed = "destroyHostileNPC";
    public const string ShipLost = "shipLossCount";
    public const string Boarded = "shipBoarded";
    public const string Fitted = "fitItemCount";
    public const string FittedHigh = "fitHiSlotCount";
    public const string FittedMedium = "fitMedSlotCount";
    public const string FittedLow = "fitLoSlotCount";
    public const string SkillInjected = "skillInjected";
    public const string TrainingStarted = "skillTrainingStarted";
    public const string IndustryStarted = "industryManufacturingJobStartedCount";
    public const string IndustryDelivered = "industryManufacturingJobDeliveredCount";
    public const string Reprocessed = "refineOreCount";
    public const string ContactAdded = "contactAdded";
}
