using Eden.Gameplay.Capsuleers;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.Server.Capsuleers;
using Eden.World.Capsuleers;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerPopulationMaintenance(
    ICapsuleerBackgroundPreparation preparation,
    TimeProvider clock,
    IPilotOperationScope operations,
    CapsuleerBackgroundBudget? budget = null
) : IDisposable
{
    private const int MaximumStepsPerTurn = 16;
    private static readonly TimeSpan ExecutionBudget = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan YieldInterval = CapsuleerCoarseRules.Window;
    private readonly PopulationStartupProgress _progress = new(ServerLog.Console.Information, clock);
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private bool _disposed;
    private int _lastFinishedSystems;

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

    internal bool Tick(CancellationToken cancellationToken = default)
    {
        using var operation = operations.Begin();
        long started = clock.GetTimestamp();
        for (int step = 0; step < MaximumStepsPerTurn; step++)
        {
            using var allowance = budget?.TryEnter("regional preparation");
            if (budget is not null && allowance is null)
            {
                return false;
            }
            cancellationToken.ThrowIfCancellationRequested();
            var turn = preparation.PrepareNext(cancellationToken);
            if (budget is not null)
            {
                budget.PendingSystems = turn.Progress.TotalSystems - turn.Progress.CompletedSystems;
            }
            if (turn.Progress.TotalSystems > 0)
            {
                _progress.Report(turn.Progress);
            }
            if (turn.Failure is { } failure)
            {
                ServerLog.Console.Warning($"NPC population: system {turn.SystemId} deferred: {failure}");
            }
            if (turn.Complete)
            {
                if (turn.Progress.CompletedSystems > _lastFinishedSystems)
                {
                    _lastFinishedSystems = turn.Progress.CompletedSystems;
                    ServerLog.Console.Information("NPC preparation finished for activated regions; other regions wait for entry.");
                }
                return true;
            }
            if (clock.GetElapsedTime(started) >= ExecutionBudget)
            {
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
                try
                {
                    Task changed = preparation.Changed;
                    if (Tick(cancellationToken))
                    {
                        await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    ServerLog.Console.Error($"NPC population background preparation failed; retrying: {error.Message}");
                }
                await Task.Delay(YieldInterval, clock, cancellationToken).ConfigureAwait(false);
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
