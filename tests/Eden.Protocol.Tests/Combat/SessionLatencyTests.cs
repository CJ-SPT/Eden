using System.Diagnostics;
using Eden.Infrastructure.Diagnostics;

namespace Eden.Protocol.Tests.Combat;

public sealed class SessionLatencyTests
{
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
