namespace Eden.Gameplay.Skills;

public readonly record struct TrainingTarget(int TypeId, int Level);

public sealed record TrainingRateChange(long Time, double PrimaryAttribute, double SecondaryAttribute);

public sealed record TrainingProgressPeriod(long StartTime, long EndTime, decimal StartPoints, decimal PointsPerMinute);

public sealed record TrainingEntry(TrainingTarget Target, long StartPoints, long EndPoints, long? StartTime, long? EndTime)
{
    public IReadOnlyList<TrainingProgressPeriod> Periods { get; init; } = [];

    public bool Equals(TrainingEntry? other) =>
        other is not null
        && Target == other.Target
        && StartPoints == other.StartPoints
        && EndPoints == other.EndPoints
        && StartTime == other.StartTime
        && EndTime == other.EndTime
        && Periods.SequenceEqual(other.Periods);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Target);
        hash.Add(StartPoints);
        hash.Add(EndPoints);
        hash.Add(StartTime);
        hash.Add(EndTime);
        foreach (var period in Periods)
        {
            hash.Add(period);
        }
        return hash.ToHashCode();
    }

    public long PointsAt(long now)
    {
        if (StartTime is not long start || EndTime is not long end || now <= start)
        {
            return StartPoints;
        }
        if (now >= end)
        {
            return EndPoints;
        }
        if (Periods.Count > 0)
        {
            var period = Periods.Last(p => p.StartTime <= now);
            decimal earned = period.StartPoints + period.PointsPerMinute * (now - period.StartTime) / TimeSpan.TicksPerMinute;
            return Math.Clamp((long)earned, StartPoints, EndPoints);
        }
        return StartPoints + (long)((decimal)(EndPoints - StartPoints) * (now - start) / (end - start));
    }
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
