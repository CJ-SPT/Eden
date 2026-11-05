namespace Eden.Protocol.Tests.Persistence;

// Retain TestClock's simulation behavior while advancing checkpoint timers deterministically.
internal sealed class CheckpointClock(TestClock clock) : TimeProvider
{
    private readonly List<CheckpointTimer> _timers = [];

    public override long TimestampFrequency => clock.TimestampFrequency;

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();

    public override long GetTimestamp() => clock.GetTimestamp();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        CheckpointTimer timer = new(this, callback, state);
        _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    internal void FireDueTimers()
    {
        foreach (var timer in _timers.ToArray())
        {
            timer.Fire();
        }
    }

    private sealed class CheckpointTimer(CheckpointClock clock, TimerCallback callback, object? state) : ITimer
    {
        private DateTimeOffset _due;
        private TimeSpan _period;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
            _period = period;
            return !_disposed;
        }

        internal void Fire()
        {
            if (_disposed || clock.GetUtcNow() < _due)
            {
                return;
            }
            _due = _period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + _period;
            callback(state);
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
