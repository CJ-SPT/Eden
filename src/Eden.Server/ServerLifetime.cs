using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Eden.Infrastructure.Diagnostics;

namespace Eden.Server;

internal sealed class ServerLifetime
{
    private readonly Stack<(string Name, Func<ValueTask> Stop)> _stops = new();

    internal void OnStop(string name, Func<ValueTask> stop) => _stops.Push((name, stop));

    internal void OnStop(string name, Action stop) =>
        OnStop(
            name,
            () =>
            {
                stop();
                return ValueTask.CompletedTask;
            }
        );

    internal static async Task RunAsync(Func<ServerLifetime, Task> run)
    {
        ServerLifetime lifetime = new();
        List<Exception> failures = [];
        try
        {
            await run(lifetime);
        }
        catch (Exception error)
        {
            failures.Add(error);
        }

        while (lifetime._stops.TryPop(out var step))
        {
            long started = Stopwatch.GetTimestamp();
            Report($"Stopping {step.Name}.");
            try
            {
                await step.Stop();
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException($"Failed to stop {step.Name}.", error));
            }
            finally
            {
                Report($"Shutdown step {step.Name} finished in {Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s.");
            }
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }
        if (failures.Count > 1)
        {
            throw new AggregateException("Eden encountered multiple host failures.", failures);
        }
    }

    private static void Report(string message)
    {
        try
        {
            ServerLog.Console.Information(message);
        }
        catch (Exception)
        {
            // Diagnostics must not prevent the remaining stop callbacks or final checkpoint.
        }
    }
}
