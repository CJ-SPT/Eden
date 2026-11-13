using System.Text;
using System.Threading.Channels;
using Eden.Infrastructure.Diagnostics;

namespace Eden.Server.Logging;

internal sealed class BufferedServerLog : IAsyncDisposable
{
    internal const int QueueCapacity = 4096;
    private static readonly TimeSpan DefaultDrainTimeout = TimeSpan.FromSeconds(2);
    private readonly Output _output;
    private readonly Output _error;
    private readonly IDisposable _redirection;
    private readonly TimeSpan _drainTimeout;
    private int _disposed;

    internal BufferedServerLog(
        ServerLog log,
        Stream output,
        Stream error,
        TimeProvider? clock = null,
        int capacity = QueueCapacity,
        TimeSpan? drainTimeout = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _drainTimeout = drainTimeout ?? DefaultDrainTimeout;
        clock ??= TimeProvider.System;
        _output = new(output, capacity, clock);
        _error = new(error, capacity, clock);
        _redirection = log.Redirect(
            (level, message) => (level is ServerLogLevel.Warning or ServerLogLevel.Error ? _error : _output).Enqueue(level, message)
        );
    }

    internal long DroppedMessages => _output.Dropped + _error.Dropped;
    internal bool DrainTimedOut { get; private set; }

    internal static BufferedServerLog Start()
    {
        System.Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new(ServerLog.Console, System.Console.OpenStandardOutput(), System.Console.OpenStandardError());
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            await Task.WhenAll(_output.Complete(), _error.Complete()).WaitAsync(_drainTimeout);
        }
        catch (TimeoutException)
        {
            DrainTimedOut = true;
        }
        finally
        {
            _redirection.Dispose();
        }
    }

    private sealed class Output
    {
        private readonly record struct Entry(DateTimeOffset Timestamp, ServerLogLevel Level, string Message);

        private readonly Channel<Entry> _queue;
        private readonly Task _drain;
        private readonly TimeProvider _clock;
        private long _dropped;

        internal Output(Stream stream, int capacity, TimeProvider clock)
        {
            _clock = clock;
            _queue = Channel.CreateBounded<Entry>(new BoundedChannelOptions(capacity) { SingleReader = true });
            _drain = Task.Run(() => DrainAsync(stream));
        }

        internal long Dropped => Interlocked.Read(ref _dropped);

        internal void Enqueue(ServerLogLevel level, string message)
        {
            if (!_queue.Writer.TryWrite(new(_clock.GetUtcNow(), level, message)))
            {
                Interlocked.Increment(ref _dropped);
            }
        }

        internal Task Complete()
        {
            _queue.Writer.TryComplete();
            return _drain;
        }

        private async Task DrainAsync(Stream stream)
        {
            try
            {
                using StreamWriter writer = new(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                long reported = 0;
                void ReportDropped()
                {
                    long dropped = Dropped;
                    if (dropped > reported)
                    {
                        writer.WriteLine($"Dropped {dropped - reported} server log messages because the output queue was full.");
                        reported = dropped;
                    }
                }

                await foreach (var entry in _queue.Reader.ReadAllAsync())
                {
                    writer.WriteLine($"{entry.Timestamp.ToLocalTime():HH:mm:ss} [{ServerLog.LevelName(entry.Level)}] {entry.Message}");
                    ReportDropped();
                }
                ReportDropped();
            }
            catch (Exception)
            {
                // A failed stream cannot report its own failure. Release queued entries and count subsequent drops.
                _queue.Writer.TryComplete();
                while (_queue.Reader.TryRead(out _))
                {
                    Interlocked.Increment(ref _dropped);
                }
            }
        }
    }
}
