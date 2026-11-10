using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;

namespace Eden.Protocol.Tests.Combat;

public sealed class SessionLatencyTests
{
    [Fact]
    public void OverlappingOwnersAreCountedAsOneElapsedInterval()
    {
        List<string> traces = [];
        using var performance = SessionPerformance.Begin("ownership union", _ => { }, force: true);
        using (new SessionLatency("union", traces.Add, traces.Add))
        {
            OwnershipMonitor first = new("first");
            OwnershipMonitor second = new("second");
            OwnershipMonitor third = new("third");
            using var outer = first.Enter("outer");
            using var middle = second.Enter("middle");
            using var inner = third.Enter("inner");
            long started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(10))
            {
                Thread.SpinWait(100);
            }
        }
        string message = Assert.Single(traces);
        double Read(string key) =>
            double.Parse(message.Split(key + "=")[1].Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Read("Ownership") <= Read("total"));
    }

    [Fact]
    public void QueuedRequestWarnsWithReplyAndNamedPhasesWhileFastRequestsStayQuiet()
    {
        List<string> warnings = [];
        using (
            var latency = new SessionLatency(
                "call ship.MachoResolveObject",
                warnings.Add,
                received: Stopwatch.GetTimestamp() - Stopwatch.Frequency
            )
        )
        {
            using (SessionLatency.Measure(SessionLatencyPhase.Handler))
            {
                SessionLatency.ObserveWorldWait("traffic maintenance", Stopwatch.GetTimestamp() - Stopwatch.Frequency);
                latency.ReplyWritten();
            }
        }
        string message = Assert.Single(warnings);
        Assert.Contains("queue=", message);
        Assert.Contains("reply=", message);
        Assert.Contains("WorldWait=", message);
        Assert.Contains("TrafficRefresh=", message);
        Assert.Contains("PendingDamage=", message);
        Assert.Contains("Handler=", message);
        Assert.Contains("observedOwner=traffic maintenance", message);
        warnings.Clear();
        using (new SessionLatency("tick", warnings.Add)) { }
        Assert.Empty(warnings);
    }
}
