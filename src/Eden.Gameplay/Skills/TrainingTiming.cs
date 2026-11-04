namespace Eden.Gameplay.Skills;

internal static class TrainingTiming
{
    // Cerebral-accelerator expirations change rate inside a queued level. Keep
    // fractional SP between periods, then round only when materializing progress.
    internal static IReadOnlyList<TrainingProgressPeriod> Plan(TrainingSkill skill, long before, long after, long start, bool alpha)
    {
        if (skill.RateChanges.Count == 0)
        {
            return [];
        }
        var changes = skill.RateChanges.OrderBy(c => c.Time).ToArray();
        var current = changes.LastOrDefault(c => c.Time <= start);
        decimal Rate(double primary, double secondary) => ((decimal)primary + (decimal)secondary / 2) * (alpha ? .5m : 1m);
        decimal rate = Rate(current?.PrimaryAttribute ?? skill.PrimaryAttribute, current?.SecondaryAttribute ?? skill.SecondaryAttribute);
        decimal points = before;
        long time = start;
        List<TrainingProgressPeriod> periods = [];
        foreach (var change in changes.Where(c => c.Time > start))
        {
            long duration = checked((long)decimal.Ceiling((after - points) * TimeSpan.TicksPerMinute / rate));
            long end = checked(time + duration);
            if (end <= change.Time)
            {
                periods.Add(new(time, end, points, rate));
                return periods;
            }
            periods.Add(new(time, change.Time, points, rate));
            points += rate * (change.Time - time) / TimeSpan.TicksPerMinute;
            time = change.Time;
            rate = Rate(change.PrimaryAttribute, change.SecondaryAttribute);
        }
        long remaining = checked((long)decimal.Ceiling((after - points) * TimeSpan.TicksPerMinute / rate));
        periods.Add(new(time, checked(time + remaining), points, rate));
        return periods;
    }
}
