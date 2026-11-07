namespace Eden.Infrastructure.Diagnostics;

internal sealed class ServerLog(bool verbose, TextWriter? output = null, TextWriter? error = null)
{
    private readonly object _gate = new();

    internal static ServerLog Console { get; } = new(Environment.GetEnvironmentVariable("EDEN_VERBOSE_LOGGING") == "1");

    public void Debug(string message)
    {
        if (verbose)
        {
            Write("DEBUG", message);
        }
    }

    public void Information(string message) => Write("INFO", message);

    public void Warning(string message) => Write("WARN", message, failure: true);

    public void Error(string message) => Write("ERROR", message, failure: true);

    // Existing motion/performance switches explicitly opt into these diagnostics.
    public void Trace(string message) => Write("TRACE", message);

    private void Write(string level, string message, bool failure = false)
    {
        lock (_gate)
        {
            var writer = failure ? error ?? System.Console.Error : output ?? System.Console.Out;
            writer.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} [{level}] {message}");
        }
    }
}
