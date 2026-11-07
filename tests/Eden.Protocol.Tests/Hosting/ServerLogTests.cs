using Eden.Infrastructure.Diagnostics;

namespace Eden.Protocol.Tests.Hosting;

public sealed class ServerLogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuietConsoleKeepsLifecycleAndFailuresWhileVerboseIncludesRoutineReports(bool verbose)
    {
        using StringWriter output = new();
        using StringWriter errors = new();
        ServerLog log = new(verbose, output, errors);
        log.Debug("Call station.GetGuests.");
        log.Debug("Notify OnMultiEvent.");
        log.Debug("World checkpoint 1 completed.");
        log.Information("Eden listening.");
        log.Warning("Slow call station.GetGuests.");
        log.Error("World checkpoint failed; resident changes remain pending.");

        string console = output.ToString();
        Assert.Contains("[INFO] Eden listening.", console);
        Assert.Equal(verbose, console.Contains("Call station.GetGuests.", StringComparison.Ordinal));
        Assert.Equal(verbose, console.Contains("Notify OnMultiEvent.", StringComparison.Ordinal));
        Assert.Equal(verbose, console.Contains("World checkpoint 1 completed.", StringComparison.Ordinal));
        Assert.DoesNotContain("Slow call", console);
        Assert.Contains("[WARN] Slow call station.GetGuests.", errors.ToString());
        Assert.Contains("[ERROR] World checkpoint failed; resident changes remain pending.", errors.ToString());
    }

    [Fact]
    public void ExplicitTracingRemainsAvailableWithQuietLogging()
    {
        using StringWriter output = new();
        ServerLog log = new(false, output);
        log.Trace("Performance tick: 2 ms.");
        log.Trace("Motion system=30000144 tick=1.");
        Assert.Contains("[TRACE] Performance tick: 2 ms.", output.ToString());
        Assert.Contains("[TRACE] Motion system=30000144 tick=1.", output.ToString());
    }
}
