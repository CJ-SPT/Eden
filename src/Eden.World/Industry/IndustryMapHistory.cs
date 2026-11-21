using Eden.Gameplay.Industry;

namespace Eden.World.Industry;

internal readonly record struct IndustryMapJobStart(int SystemId, IndustryActivity Activity);

// The resident economy owns this index and publishes changes only after validating the job transaction.
internal sealed class IndustryMapHistory
{
    private sealed record Snapshot(
        long From,
        long Until,
        IReadOnlyList<IndustryMapJobCount> All,
        IReadOnlyDictionary<IndustryActivity, IReadOnlyList<IndustryMapJobCount>> ByActivity
    );

    private readonly SortedDictionary<(long StartTime, long JobId), IndustryMapJobStart> _jobs = [];
    private Snapshot? _snapshot;

    internal void Clear()
    {
        _jobs.Clear();
        _snapshot = null;
    }

    internal void Record(long jobId, long startTime, IndustryMapJobStart start, long now)
    {
        Expire(now);
        _jobs.Add((startTime, jobId), start);
        _snapshot = null;
    }

    internal void Remove(long jobId, long startTime)
    {
        if (_jobs.Remove((startTime, jobId)))
        {
            _snapshot = null;
        }
    }

    internal IReadOnlyList<IndustryMapJobCount> Read(IndustryActivity? activity, long now)
    {
        if (_snapshot is not { } snapshot || now < snapshot.From || now >= snapshot.Until)
        {
            Expire(now);
            snapshot = _snapshot = BuildSnapshot(now);
        }
        return activity is { } selected ? snapshot.ByActivity.GetValueOrDefault(selected) ?? [] : snapshot.All;
    }

    private void Expire(long now)
    {
        long cutoff = now - TimeSpan.TicksPerDay;
        while (_jobs.Count > 0)
        {
            var oldest = _jobs.First().Key;
            if (oldest.StartTime > cutoff)
            {
                break;
            }
            _jobs.Remove(oldest);
            _snapshot = null;
        }
    }

    private Snapshot BuildSnapshot(long now)
    {
        Dictionary<int, int> all = [];
        Dictionary<IndustryActivity, Dictionary<int, int>> byActivity = [];
        long until = long.MaxValue;
        foreach (var (key, start) in _jobs)
        {
            if (key.StartTime > now)
            {
                until = Math.Min(until, key.StartTime);
                break;
            }
            until = Math.Min(until, key.StartTime + TimeSpan.TicksPerDay);
            all[start.SystemId] = all.GetValueOrDefault(start.SystemId) + 1;
            if (!byActivity.TryGetValue(start.Activity, out var counts))
            {
                byActivity.Add(start.Activity, counts = []);
            }
            counts[start.SystemId] = counts.GetValueOrDefault(start.SystemId) + 1;
        }
        return new(now, until, Rows(all), byActivity.ToDictionary(pair => pair.Key, pair => Rows(pair.Value)));
    }

    private static IReadOnlyList<IndustryMapJobCount> Rows(Dictionary<int, int> counts) =>
        Array.AsReadOnly(counts.OrderBy(pair => pair.Key).Select(pair => new IndustryMapJobCount(pair.Key, pair.Value)).ToArray());
}
