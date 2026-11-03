namespace Eden.Gameplay.Skills;

public readonly record struct TrainingTarget(int TypeId, int Level);

public sealed record TrainingEntry(TrainingTarget Target, long StartPoints, long EndPoints, long? StartTime, long? EndTime)
{
    public long PointsAt(long now) =>
        StartTime is long start && EndTime is long end
            ? StartPoints + (long)((decimal)(EndPoints - StartPoints) * Math.Clamp(now - start, 0, end - start) / (end - start))
            : StartPoints;
}

public sealed record TrainingEvent(long Time, int TypeId, long Points, int EventId);

public sealed record TrainingState(IReadOnlyList<TrainingEntry> Queue, IReadOnlyList<int> ChangedSkills);

public interface IPilotTraining
{
    TrainingState Advance(long pilotId);
    TrainingState Save(long pilotId, IReadOnlyList<TrainingTarget> targets, bool activate);
    TrainingState Pause(long pilotId);
    IReadOnlyList<TrainingEvent> History(long pilotId, int count);
}

public sealed class InvalidTrainingQueue(string reason) : Exception(reason);
