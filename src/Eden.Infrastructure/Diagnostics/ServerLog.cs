namespace Eden.Infrastructure.Diagnostics;

internal enum ServerLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
    Trace,
}

internal sealed class ServerLog(bool verbose, TextWriter? output = null, TextWriter? error = null)
{
    private readonly object _gate = new();
    private Action<ServerLogLevel, string>? _sink;

    internal static ServerLog Console { get; } = new(Environment.GetEnvironmentVariable("EDEN_VERBOSE_LOGGING") == "1");

    public void Debug(string message)
    {
        if (verbose)
        {
            Write(ServerLogLevel.Debug, message);
        }
    }

    public void Information(string message) => Write(ServerLogLevel.Information, message);

    public void Warning(string message) => Write(ServerLogLevel.Warning, message);

    public void Error(string message) => Write(ServerLogLevel.Error, message);

    // Existing motion/performance switches explicitly opt into these diagnostics.
    public void Trace(string message) => Write(ServerLogLevel.Trace, message);

    internal IDisposable Redirect(Action<ServerLogLevel, string> sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (Interlocked.CompareExchange(ref _sink, sink, null) is not null)
        {
            throw new InvalidOperationException("The server log already has an output sink.");
        }
        return new Redirection(this, sink);
    }

    private sealed class Redirection(ServerLog log, Action<ServerLogLevel, string> sink) : IDisposable
    {
        public void Dispose() => Interlocked.CompareExchange(ref log._sink, null, sink);
    }

    internal static string LevelName(ServerLogLevel level) =>
        level switch
        {
            ServerLogLevel.Debug => "DEBUG",
            ServerLogLevel.Information => "INFO",
            ServerLogLevel.Warning => "WARN",
            ServerLogLevel.Error => "ERROR",
            ServerLogLevel.Trace => "TRACE",
            _ => throw new ArgumentOutOfRangeException(nameof(level)),
        };

    private void Write(ServerLogLevel level, string message)
    {
        if (Volatile.Read(ref _sink) is { } sink)
        {
            sink(level, message);
            return;
        }
        lock (_gate)
        {
            bool failure = level is ServerLogLevel.Warning or ServerLogLevel.Error;
            var writer = failure ? error ?? System.Console.Error : output ?? System.Console.Out;
            writer.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} [{LevelName(level)}] {message}");
        }
    }
}
