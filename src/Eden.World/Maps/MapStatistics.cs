using System.Collections.Immutable;
using Eden.Gameplay.Exploration;
using Eden.Gameplay.Maps;

namespace Eden.World.Maps;

internal readonly record struct MapActivityEvent(long Time, int SystemId, MapActivityKind Kind);

internal readonly struct MapActivityBatch(ImmutableList<MapActivityEvent[]> completed, ReadOnlyMemory<MapActivityEvent> tail)
{
    internal IEnumerable<MapActivityEvent> Read()
    {
        foreach (var block in completed)
        {
            foreach (var activity in block)
            {
                yield return activity;
            }
        }
        for (int index = 0; index < tail.Length; index++)
        {
            yield return tail.Span[index];
        }
    }
}

internal sealed record MapPilotSample(long Time, IReadOnlyDictionary<int, MapPilotCounts> Systems);

internal sealed record MapVisitHistory(int LastSystem, ImmutableSortedDictionary<int, MapSystemVisit> Systems);

internal sealed class MapStatistics
{
    // Pinned idCheckers.IsKnownSpaceSystem, excluding the base ID that has no system.
    private const int FirstKnownSystem = 30_000_001;
    private const int LastKnownSystem = 30_999_999;
    private const int MaximumHistoryHours = 24;
    private const long HistoryRetention = MaximumHistoryHours * TimeSpan.TicksPerHour;
    private const long PopulationRetention = 30 * TimeSpan.TicksPerMinute;
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(1);

    private sealed record HistoryCache(long Revision, long From, long Until, MapHistorySnapshot Snapshot);

    private sealed class ActivityMinute
    {
        private const int InitialBlockSize = 16;
        private const int MaximumBlockSize = 256;

        private ImmutableList<MapActivityEvent[]> _completed = [];
        private MapActivityEvent[] _tail = new MapActivityEvent[InitialBlockSize];
        private int _count;

        internal void Add(MapActivityEvent activity)
        {
            if (_count == _tail.Length)
            {
                var next = new MapActivityEvent[Math.Min(_tail.Length * 2, MaximumBlockSize)];
                _completed = _completed.Add(_tail);
                _tail = next;
                _count = 0;
            }
            _tail[_count++] = activity;
        }

        // An existing snapshot sees only the prefix written before its capture.
        internal MapActivityBatch Capture() => new(_completed, _tail.AsMemory(0, _count));
    }

    private readonly object _gate = new();
    private readonly SortedDictionary<long, ActivityMinute> _events = [];
    private ImmutableQueue<MapPilotSample> _samples = ImmutableQueue<MapPilotSample>.Empty;
    private ImmutableDictionary<long, MapVisitHistory> _visits = ImmutableDictionary<long, MapVisitHistory>.Empty;
    private long _lastSampleMinute = long.MinValue;
    private readonly Dictionary<(MapHistoryStatistic Statistic, int Hours), HistoryCache> _history = [];
    private long _jumpsRevision;
    private long _killsRevision;
    private long _populationFrom;
    private long _populationUntil;
    private MapPilotPopulation _population = MapPilotPopulation.Empty;

    internal static bool IsKnownSpace(int systemId) => systemId is >= FirstKnownSystem and <= LastKnownSystem;

    internal bool RecordVisit(long pilot, int system, long now)
    {
        bool onMap = IsKnownSpace(system) || WormholeRules.IsWormholeSystem(system);
        if (!onMap && !AbyssalRules.IsSystem(system))
        {
            return false;
        }
        lock (_gate)
        {
            var history = _visits.GetValueOrDefault(pilot);
            if (!onMap)
            {
                // The return is a new visit. Keep the boundary without storing the private location ID.
                if (history is not { LastSystem: > 0 })
                {
                    return false;
                }
                _visits = _visits.SetItem(pilot, history with { LastSystem = 0 });
                return true;
            }
            var systems = history?.Systems ?? ImmutableSortedDictionary<int, MapSystemVisit>.Empty;
            var previous = systems.GetValueOrDefault(system);
            int count = previous.Visits + (history?.LastSystem == system ? 0 : 1);
            var visit = new MapSystemVisit(system, count, Math.Max(now, previous.LastVisitedAt));
            if (visit == previous)
            {
                return false;
            }
            _visits = _visits.SetItem(pilot, new(system, systems.SetItem(system, visit)));
            return true;
        }
    }

    internal IReadOnlyList<MapSystemVisit> SystemVisits(long pilot)
    {
        MapVisitHistory? history;
        lock (_gate)
        {
            history = _visits.GetValueOrDefault(pilot);
        }
        return history is null ? [] : Array.AsReadOnly(history.Systems.Values.ToArray());
    }

    internal bool ClearSystemVisits(long pilot)
    {
        lock (_gate)
        {
            if (!_visits.ContainsKey(pilot))
            {
                return false;
            }
            _visits = _visits.Remove(pilot);
            return true;
        }
    }

    internal void Record(MapActivityEvent activity)
    {
        lock (_gate)
        {
            long minute = Minute(activity.Time);
            if (!_events.TryGetValue(minute, out var events))
            {
                events = new();
                _events.Add(minute, events);
            }
            events.Add(activity);
            if (activity.Kind == MapActivityKind.Jump)
            {
                _jumpsRevision++;
            }
            else
            {
                _killsRevision++;
            }
        }
    }

    internal MapHistorySnapshot History(MapHistoryStatistic statistic, int hours, long now)
    {
        hours = Math.Min(hours, MaximumHistoryHours);
        long window = hours * TimeSpan.TicksPerHour;
        long cutoff = now - window;
        var key = (statistic, hours);
        HistoryCache? previous;
        MapActivityBatch[] snapshotEvents;
        long revision;
        lock (_gate)
        {
            previous = _history.GetValueOrDefault(key);
            revision = Revision(statistic);
            if (previous is not null && previous.Revision == revision && now >= previous.From && now < previous.Until)
            {
                return previous.Snapshot;
            }
            long firstMinute = Minute(cutoff);
            snapshotEvents = _events.Where(entry => entry.Key >= firstMinute).Select(entry => entry.Value.Capture()).ToArray();
        }
        Dictionary<int, MapActivityCounts> totals = [];
        long until = long.MaxValue;
        foreach (var batch in snapshotEvents)
        {
            foreach (var activity in batch.Read())
            {
                if (activity.Time <= cutoff || Statistic(activity.Kind) != statistic)
                {
                    continue;
                }
                if (activity.Time > now)
                {
                    until = Math.Min(until, activity.Time);
                    continue;
                }
                until = Math.Min(until, activity.Time + window);
                totals[activity.SystemId] = totals.GetValueOrDefault(activity.SystemId).Record(activity.Kind);
            }
        }
        var rows = totals.OrderBy(pair => pair.Key).Select(pair => new MapHistoryRow(pair.Key, pair.Value)).ToArray();
        var snapshot =
            rows.Length == 0 ? MapHistorySnapshot.Empty
            : previous is not null && rows.SequenceEqual(previous.Snapshot.Rows) ? previous.Snapshot
            : new(now, Array.AsReadOnly(rows));
        lock (_gate)
        {
            if (revision == Revision(statistic) && ReferenceEquals(previous, _history.GetValueOrDefault(key)))
            {
                _history[key] = new(revision, now, until, snapshot);
            }
        }
        return snapshot;
    }

    internal bool Sample(long now, IReadOnlyDictionary<int, MapPilotCounts> counts)
    {
        lock (_gate)
        {
            long minute = Minute(now);
            if (minute <= _lastSampleMinute)
            {
                return false;
            }
            _lastSampleMinute = minute;
            TrimSamples(now);
            _samples = _samples.Enqueue(new(now, counts));
            _populationUntil = 0;
            return counts.Count > 0 || _samples.Any(sample => sample.Systems.Count > 0);
        }
    }

    internal MapPilotPopulation Population(long now)
    {
        lock (_gate)
        {
            if (now >= _populationFrom && now < _populationUntil)
            {
                return _population;
            }
            Dictionary<int, MapPilotCounts> sums = [];
            int samples = 0;
            long until = long.MaxValue;
            foreach (var sample in _samples)
            {
                long expires = sample.Time + PopulationRetention;
                if (expires <= now)
                {
                    continue;
                }
                if (sample.Time > now)
                {
                    until = Math.Min(until, sample.Time);
                    break;
                }
                samples++;
                until = Math.Min(until, expires);
                foreach (var (system, count) in sample.Systems)
                {
                    sums[system] = sums.GetValueOrDefault(system).Add(count);
                }
            }
            _populationFrom = now;
            _populationUntil = until;
            _population = sums.Count == 0 ? MapPilotPopulation.Empty : new(samples, sums.ToImmutableDictionary());
            return _population;
        }
    }

    internal MapStatisticsCheckpoint Capture(long now)
    {
        lock (_gate)
        {
            long cutoff = now - HistoryRetention;
            long firstMinute = Minute(cutoff);
            foreach (long expired in _events.Keys.TakeWhile(minute => minute < firstMinute).ToArray())
            {
                _events.Remove(expired);
            }
            TrimSamples(now);
            return new(_events.Values.Select(static minute => minute.Capture()).ToArray(), _samples, cutoff, _visits);
        }
    }

    internal void Restore(MapStatisticsPayload saved, long now)
    {
        _visits = saved.Visits ?? ImmutableDictionary<long, MapVisitHistory>.Empty;
        RestoreEvents(saved.Events, now);
        foreach (var sample in saved.Samples)
        {
            if (sample.Time > now - PopulationRetention)
            {
                Sample(sample.Time, sample.Systems);
            }
        }
    }

    internal void RestoreEvents(IEnumerable<MapActivityEvent> events, long now)
    {
        long cutoff = now - HistoryRetention;
        foreach (var activity in events)
        {
            if (activity.Time > cutoff)
            {
                Record(activity);
            }
        }
    }

    private void TrimSamples(long now)
    {
        while (!_samples.IsEmpty && _samples.Peek().Time <= now - PopulationRetention)
        {
            _samples = _samples.Dequeue();
        }
    }

    private static long Minute(long time) => time / TimeSpan.TicksPerMinute;

    private long Revision(MapHistoryStatistic statistic) => statistic == MapHistoryStatistic.Jumps ? _jumpsRevision : _killsRevision;

    private static MapHistoryStatistic Statistic(MapActivityKind kind) =>
        kind == MapActivityKind.Jump ? MapHistoryStatistic.Jumps : MapHistoryStatistic.Kills;
}
