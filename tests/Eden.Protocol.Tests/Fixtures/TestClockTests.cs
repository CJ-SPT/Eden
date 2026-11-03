namespace Eden.Protocol.Tests.Fixtures;

public sealed class TestClockTests
{
    [Fact]
    public void WallAndElapsedTimeCanAdvanceIndependently()
    {
        DateTimeOffset start = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        TestClock clock = new(start);
        long timestamp = clock.GetTimestamp();
        clock.AdvanceWallTime(TimeSpan.FromHours(1));
        Assert.Equal(timestamp, clock.GetTimestamp());
        clock.AdvanceElapsed(TimeSpan.FromSeconds(3));
        Assert.Equal(start.AddHours(1), clock.GetUtcNow());
        Assert.Equal(TimeSpan.FromSeconds(3), clock.GetElapsedTime(timestamp, clock.GetTimestamp()));
        clock.Milliseconds += 2000;
        Assert.Equal(start.AddHours(1).AddSeconds(2), clock.GetUtcNow());
    }
}
