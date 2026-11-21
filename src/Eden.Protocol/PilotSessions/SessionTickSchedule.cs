namespace Eden.Protocol.PilotSessions;

internal sealed class SessionTickSchedule(TimeProvider clock)
{
    private long _next = clock.GetTimestamp() + clock.TimestampFrequency;
    private long? _resumedAt;
    private bool _idle;

    public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0, (_next - clock.GetTimestamp()) / (double)clock.TimestampFrequency));

    public TimeSpan StartDelay(long timestamp)
    {
        long due = _resumedAt is long resumed ? Math.Max(_next, resumed) : _next;
        return timestamp > due ? clock.GetElapsedTime(due, timestamp) : TimeSpan.Zero;
    }

    public void PauseMeasurement() => _idle = true;

    public void ResumeMeasurement()
    {
        if (_idle)
        {
            // A session can stop requesting ticks while docked. Idle time is not frame lateness.
            _resumedAt = clock.GetTimestamp();
            _idle = false;
        }
    }

    public void Complete()
    {
        // Retain the cadence after slow work, skipping expired wakeups without replaying paid cycles.
        long now = clock.GetTimestamp();
        long periods = Math.Max(1, (now - _next) / clock.TimestampFrequency + 1);
        _next += periods * clock.TimestampFrequency;
        _resumedAt = null;
    }
}
