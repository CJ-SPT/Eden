using System.Collections.Immutable;

namespace Eden.Infrastructure.Diagnostics;

public readonly record struct SessionFrameInterval(
    long Count,
    double AverageMilliseconds,
    double MaximumMilliseconds,
    double MaximumStartDelayMilliseconds
);

public sealed class SessionFrameMonitor(TimeProvider clock)
{
    public const int IntervalSeconds = 2;
    public const int HistoryLength = 60;

    private readonly object _gate = new();
    private readonly long _started = clock.GetTimestamp();

    // Reserve an extra slot for the current interval so readers can see all completed intervals.
    private readonly Interval[] _intervals = new Interval[HistoryLength + 1];

    public void Record(TimeSpan elapsed, TimeSpan startDelay)
    {
        lock (_gate)
        {
            long number = CurrentInterval();
            ref var interval = ref _intervals[number % _intervals.Length];
            if (interval.Number != number)
            {
                interval = new() { Number = number };
            }
            interval.Count++;
            interval.TotalMilliseconds += elapsed.TotalMilliseconds;
            interval.MaximumMilliseconds = Math.Max(interval.MaximumMilliseconds, elapsed.TotalMilliseconds);
            interval.MaximumStartDelayMilliseconds = Math.Max(interval.MaximumStartDelayMilliseconds, startDelay.TotalMilliseconds);
        }
    }

    public ImmutableArray<SessionFrameInterval> Read()
    {
        var history = ImmutableArray.CreateBuilder<SessionFrameInterval>(HistoryLength);
        lock (_gate)
        {
            long current = CurrentInterval();
            for (long number = current - HistoryLength; number < current; number++)
            {
                if (number >= 0 && _intervals[number % _intervals.Length] is { Count: > 0 } interval && interval.Number == number)
                {
                    history.Add(
                        new(
                            interval.Count,
                            interval.TotalMilliseconds / interval.Count,
                            interval.MaximumMilliseconds,
                            interval.MaximumStartDelayMilliseconds
                        )
                    );
                }
                else
                {
                    history.Add(default);
                }
            }
        }
        return history.MoveToImmutable();
    }

    private long CurrentInterval() => clock.GetElapsedTime(_started).Ticks / (IntervalSeconds * TimeSpan.TicksPerSecond);

    private struct Interval
    {
        public long Number;
        public long Count;
        public double TotalMilliseconds;
        public double MaximumMilliseconds;
        public double MaximumStartDelayMilliseconds;
    }
}
