namespace Eden.Protocol.Tests.Fixtures;

internal sealed class TestClock : TimeProvider
{
    private long _milliseconds;

    public TestClock(DateTimeOffset? start = null) => UtcNow = start ?? DateTimeOffset.UtcNow;

    public DateTimeOffset UtcNow { get; set; }
    public bool AdvanceWallTimeWithElapsed { get; set; } = true;
    public long Milliseconds
    {
        get => _milliseconds;
        set
        {
            if (AdvanceWallTimeWithElapsed)
            {
                UtcNow = UtcNow.AddMilliseconds(value - _milliseconds);
            }
            _milliseconds = value;
        }
    }
    public long Seconds
    {
        get => Milliseconds / 1000;
        set => Milliseconds = checked(value * 1000);
    }
    public long Now => UtcNow.UtcDateTime.ToFileTimeUtc();
    public override long TimestampFrequency => 1000;

    public override long GetTimestamp() => Milliseconds;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public void AdvanceWallTime(TimeSpan elapsed) => UtcNow += elapsed;

    public void AdvanceElapsed(TimeSpan elapsed) => _milliseconds = checked(_milliseconds + (long)elapsed.TotalMilliseconds);
}
