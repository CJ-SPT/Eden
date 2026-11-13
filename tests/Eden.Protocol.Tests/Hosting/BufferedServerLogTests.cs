using System.Text;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Tests.Fixtures;
using Eden.Server.Logging;

namespace Eden.Protocol.Tests.Hosting;

public sealed class BufferedServerLogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BufferedOutputPreservesLevelsQuietModeUnicodeAndFlushesAtShutdown(bool verbose)
    {
        using MemoryStream output = new();
        using MemoryStream errors = new();
        using StringWriter fallback = new();
        TestClock clock = new(new DateTimeOffset(2026, 11, 9, 12, 34, 56, TimeSpan.Zero));
        ServerLog log = new(verbose, fallback, fallback);
        var buffered = new BufferedServerLog(log, output, errors, clock);
        string timestamp = clock.UtcNow.ToLocalTime().ToString("HH:mm:ss");
        log.Information("Pilot 星 🚀 connected.");
        log.Debug("Routine call.");
        log.Trace("Explicit trace.");
        log.Warning("Slow call.");
        log.Error("Save failed.");
        clock.AdvanceWallTime(TimeSpan.FromHours(1));
        await buffered.DisposeAsync();
        await buffered.DisposeAsync();

        string text = Encoding.UTF8.GetString(output.ToArray());
        string failures = Encoding.UTF8.GetString(errors.ToArray());
        Assert.Contains($"{timestamp} [INFO] Pilot 星 🚀 connected.", text);
        Assert.Equal(verbose, text.Contains("[DEBUG] Routine call.", StringComparison.Ordinal));
        Assert.Contains($"{timestamp} [TRACE] Explicit trace.", text);
        Assert.DoesNotContain("Slow call", text);
        Assert.Contains($"{timestamp} [WARN] Slow call.", failures);
        Assert.Contains($"{timestamp} [ERROR] Save failed.", failures);
        Assert.DoesNotContain("connected", failures);
        log.Information("Restored output.");
        Assert.Contains("[INFO] Restored output.", fallback.ToString());
    }

    [Fact]
    public async Task SlowOutputDoesNotBlockProducersWhileQueueHasRoomAndShutdownDrainsEveryEntry()
    {
        using DelayedOutput output = new();
        using MemoryStream errors = new();
        ServerLog log = new(false);
        var buffered = new BufferedServerLog(log, output, errors, capacity: 4);
        try
        {
            log.Information("First entry.");
            await output.Writing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Run(() =>
                {
                    log.Information("Second entry.");
                    log.Information("Third entry.");
                    log.Warning("Independent error output.");
                })
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(output.Release.IsSet);
        }
        finally
        {
            output.Release.Set();
            await buffered.DisposeAsync();
        }
        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("First entry.", text);
        Assert.Contains("Second entry.", text);
        Assert.Contains("Third entry.", text);
        Assert.True(text.IndexOf("First entry.", StringComparison.Ordinal) < text.IndexOf("Second entry.", StringComparison.Ordinal));
        Assert.Contains("[WARN] Independent error output.", Encoding.UTF8.GetString(errors.ToArray()));
    }

    [Fact]
    public async Task FullQueueDropsLogsWithoutBlockingWorldWorkOrCheckpointing()
    {
        using TestWorld world = new(liveWorld: true);
        using DelayedOutput output = new();
        using MemoryStream errors = new();
        ServerLog log = new(false);
        await using var buffered = new BufferedServerLog(log, output, errors, capacity: 1);
        try
        {
            log.Information("Start.");
            await output.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Run(async () =>
                {
                    for (int index = 0; index < 12; index++)
                    {
                        log.Information($"Queued entry {index}.");
                    }
                    var pilot = world.Store.FindPilot(1000001)!;
                    world.Store.SetPosition(pilot.ShipId, new(1000, 2000, 3000), 30000142);
                    await Eden.World.Runtime.LiveWorld.Find(world.Path)!.FlushAsync();
                })
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(11, buffered.DroppedMessages);
        }
        finally
        {
            output.Release.Set();
        }
        await buffered.DisposeAsync();
        string text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("Queued entry 0.", text);
        Assert.Contains("Dropped 11 server log messages", text);
    }

    [Fact]
    public async Task ShutdownStopsWaitingForBlockedOutput()
    {
        using DelayedOutput output = new();
        using MemoryStream errors = new();
        ServerLog log = new(false);
        await using var buffered = new BufferedServerLog(log, output, errors, drainTimeout: TimeSpan.FromMilliseconds(50));
        try
        {
            log.Information("Blocked entry.");
            await output.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await buffered.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(buffered.DrainTimedOut);
        }
        finally
        {
            output.Release.Set();
        }
    }

    private sealed class DelayedOutput : MemoryStream
    {
        internal TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writing.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test did not release its output stream.");
            }
            base.Write(buffer, offset, count);
        }
    }
}
