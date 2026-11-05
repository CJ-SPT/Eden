namespace Eden.Protocol.PilotSessions;

internal sealed class SessionTickSchedule(TimeProvider clock)
{
    private long _next = clock.GetTimestamp() + clock.TimestampFrequency;

    public TimeSpan Remaining => TimeSpan.FromSeconds(Math.Max(0, (_next - clock.GetTimestamp()) / (double)clock.TimestampFrequency));

    public void Complete()
    {
        // Retain the cadence after slow work, skipping expired wakeups without replaying paid cycles.
        long now = clock.GetTimestamp();
        long periods = Math.Max(1, (now - _next) / clock.TimestampFrequency + 1);
        _next += periods * clock.TimestampFrequency;
    }
}
