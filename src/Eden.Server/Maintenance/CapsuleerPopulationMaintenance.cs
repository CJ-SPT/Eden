using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerPopulationMaintenance(
    ICapsuleerBackgroundPreparation preparation,
    TimeProvider clock,
    IPilotOperationScope operations
) : IDisposable
{
    private const int MaximumStepsPerTurn = 16;
    private static readonly TimeSpan ExecutionBudget = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan YieldInterval = TimeSpan.FromMilliseconds(50);
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;

    internal void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_cancellation is null)
        {
            _cancellation = new();
            // No planning or world work executes synchronously on the listener startup path.
            _loop = Task.Run(() => Run(_cancellation.Token));
        }
    }

    internal bool Tick(CancellationToken cancellationToken = default) => Tick(out _, cancellationToken);

    private bool Tick(out TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        retryAfter = null;
        using var operation = operations.Begin();
        long started = clock.GetTimestamp();
        for (int step = 0; step < MaximumStepsPerTurn; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var turn = preparation.PrepareNext(cancellationToken);
            if (turn.Failure is { } failure)
            {
                ServerLog.Console.Warning($"NPC population: system {turn.SystemId} deferred: {failure}");
            }
            if (turn.Complete)
            {
                return true;
            }
            if (turn.RetryLater || clock.GetElapsedTime(started) >= ExecutionBudget)
            {
                retryAfter = turn.RetryAfter;
                break;
            }
        }
        return false;
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TimeSpan? retryAfter = null;
                Task changed = preparation.Changed;
                try
                {
                    if (Tick(out retryAfter, cancellationToken))
                    {
                        await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ServerLog.Console.Error($"NPC population preparation failed; retrying: {error.Message}");
                }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    var delay = retryAfter is { } retry && retry > YieldInterval ? retry : YieldInterval;
                    var timer = Task.Delay(delay, clock, wait.Token);
                    await Task.WhenAny(timer, changed).WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    // A new occupied system cancels a later retry timer immediately.
                    wait.Cancel();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        _disposed = true;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        _loop.GetAwaiter().GetResult();
        cancellation?.Dispose();
    }
}
