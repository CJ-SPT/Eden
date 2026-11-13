using System.Runtime.ExceptionServices;

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
            try
            {
                await step.Stop();
            }
            catch (Exception error)
            {
                failures.Add(new InvalidOperationException($"Failed to stop {step.Name}.", error));
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
}
