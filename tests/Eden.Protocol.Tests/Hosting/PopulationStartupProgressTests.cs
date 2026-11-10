using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Capsuleers;

namespace Eden.Protocol.Tests.Hosting;

public sealed class PopulationStartupProgressTests
{
    [Fact]
    public void BarReportsRealCountsAndDeferredSystemsWithoutFloodingTheLog()
    {
        List<string> lines = [];
        PopulationStartupProgress progress = new(lines.Add, new TestClock());
        progress.Report(new(0, 100, 0));
        progress.Report(new(1, 100, 0));
        progress.Report(new(4, 100, 0));
        progress.Report(new(5, 100, 1));
        progress.Report(new(100, 100, 2));
        progress.Report(new(100, 100, 2));

        Assert.Equal(3, lines.Count);
        Assert.Contains("[--------------------]   0% (0/100 systems checked; 0 deferred)", lines[0]);
        Assert.Contains("[#-------------------]   5% (5/100 systems checked; 1 deferred)", lines[1]);
        Assert.Contains("[####################] 100% (100/100 systems checked; 2 deferred)", lines[2]);
        Assert.All(lines, line => Assert.DoesNotContain('\r', line));
    }

    [Fact]
    public void SlowPreparationRefreshesCountsEvenWithinTheSameBarSegment()
    {
        List<string> lines = [];
        TestClock clock = new();
        PopulationStartupProgress progress = new(lines.Add, clock);
        progress.Report(new(0, 1000, 0));
        clock.AdvanceElapsed(TimeSpan.FromSeconds(1));
        progress.Report(new(1, 1000, 0));
        progress.Report(new(2, 1000, 0));
        Assert.Equal(2, lines.Count);
        Assert.Contains("1/1000 systems checked", lines[1]);
    }

    [Fact]
    public void EmptyAndFullyCachedCatalogsCompleteImmediately()
    {
        List<string> lines = [];
        new PopulationStartupProgress(lines.Add).Report(new(0, 0, 0));
        new PopulationStartupProgress(lines.Add).Report(new(8490, 8490, 0));
        Assert.All(lines, line => Assert.Contains("[####################] 100%", line));
        Assert.Contains("0/0 systems checked", lines[0]);
        Assert.Contains("8490/8490 systems checked", lines[1]);
    }
}
