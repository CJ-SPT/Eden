using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Server.Chat;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMaintenance(
    ICapsuleerScheduler scheduler,
    ICapsuleerHandoff handoff,
    ICapsuleerScene scene,
    ChatRuntime chat,
    TimeProvider clock,
    IWorldSynchronization synchronization,
    ICapsuleerUpdateDiagnostics updateDiagnostics,
    IPilotOperationScope? operations = null,
    ICapsuleerDiagnostics? diagnostics = null
) : IDisposable, ICapsuleerPresentation
{
    // System.Threading.Timer bounds one wait to uint.MaxValue - 1 milliseconds.
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    private static readonly TimeSpan CatchUpBudget = CapsuleerJobQueue.WakeInterval / 2;
    private CancellationTokenSource? _cancellation;
    private Task _loop = Task.CompletedTask;
    private volatile bool _stopping;
    private long _waitTicks;
    private readonly Dictionary<long, ChatPilot> _lastPresence = [];
    private readonly List<ChatPilot> _presenceChanges = [];
    private CapsuleerBatch? _retryPresentation;
    private CapsuleerBatch? _publishedPresentation;

    internal Task Completion => _loop;
    internal TimeSpan WorkerWait => TimeSpan.FromTicks(Interlocked.Read(ref _waitTicks));

    internal void Start(CancellationToken stopping = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (_cancellation is null)
        {
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            _loop = Run(_cancellation.Token);
        }
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        var wake = CapsuleerWakePlan.Polling;
        try
        {
            wake = scheduler.NextWake();
        }
        catch (Exception error)
        {
            ServerLog.Console.Error($"Capsuleer simulation failed: {error.Message}");
        }
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                long waiting = clock.GetTimestamp();
                await Wait(wake, cancellationToken).ConfigureAwait(false);
                Interlocked.Add(ref _waitTicks, clock.GetElapsedTime(waiting).Ticks);
                cancellationToken.ThrowIfCancellationRequested();
                var changedDuringTurn = scheduler.NextWake().Changed;
                var budget = CapsuleerJobQueue.ExecutionBudget;
                bool backlogged;
                do
                {
                    long started = clock.GetTimestamp();
                    Tick(budget, out backlogged, out bool advanced, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (backlogged)
                    {
                        var interval = CapsuleerJobQueue.WakeInterval;
                        var worked = clock.GetElapsedTime(started);
                        var remaining = advanced ? interval - worked : interval;
                        var delay = remaining > CapsuleerJobQueue.ExecutionBudget ? remaining : CapsuleerJobQueue.ExecutionBudget;
                        budget = advanced ? CatchUpBudget : CapsuleerJobQueue.ExecutionBudget;
                        waiting = clock.GetTimestamp();
                        if (advanced && diagnostics?.Read().OldestDelayTicks > interval.Ticks)
                        {
                            // Each job releases the owner. Yield to runnable requests between
                            // productive slices while catching up more than one wake interval.
                            await Task.Yield();
                        }
                        else
                        {
                            await Task.Delay(delay, clock, cancellationToken).ConfigureAwait(false);
                        }
                        Interlocked.Add(ref _waitTicks, clock.GetElapsedTime(waiting).Ticks);
                    }
                } while (backlogged);
                wake = scheduler.NextWake();
                if (changedDuringTurn.IsCompleted)
                {
                    // A commit or membership change between bounded jobs must receive another turn.
                    wake = wake with
                    {
                        Delay = TimeSpan.Zero,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Capsuleer simulation failed: {error.Message}");
                // Failed actions remain due. Retain the old bounded retry interval instead of spinning.
                wake = CapsuleerWakePlan.Polling;
            }
        }
    }

    private async Task Wait(CapsuleerWakePlan wake, CancellationToken cancellationToken)
    {
        if (wake.Delay is not { } delay)
        {
            await wake.Changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        if (delay <= TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            return;
        }
        // Batch nearby deadlines within the existing 50 ms maintenance resolution.
        // A large staggered population must not turn one-shot timers into per-agent wakes.
        if (delay < CapsuleerJobQueue.WakeInterval)
        {
            delay = CapsuleerJobQueue.WakeInterval;
        }
        if (delay > MaximumTimerDelay)
        {
            delay = MaximumTimerDelay;
        }
        TaskCompletionSource elapsed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timer = clock.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            elapsed,
            delay,
            Timeout.InfiniteTimeSpan
        );
        try
        {
            var ready = await Task.WhenAny(elapsed.Task, wake.Changed).WaitAsync(cancellationToken).ConfigureAwait(false);
            await ready.ConfigureAwait(false);
        }
        finally
        {
            // A canceled wait must also release WhenAny's change subscription.
            elapsed.TrySetCanceled();
        }
    }

    internal MinerObservation? Tick() => Tick(CapsuleerJobQueue.ExecutionBudget, out _, out _);

    private MinerObservation? Tick(TimeSpan budget, out bool backlogged, out bool advanced, CancellationToken cancellationToken = default)
    {
        using var operation = operations?.Begin();
        using var performance = SessionPerformance.IsActive
            ? SessionPerformance.Phase("traffic maintenance")
            : SessionPerformance.Begin("traffic maintenance", ServerLog.Console.Trace);
        long started = clock.GetTimestamp();
        try
        {
            MinerObservation? lastMiner = null;
            int completed = 0;
            do
            {
                var batch = scheduler.RunMaintenance(scene.Stamp, CapsuleerJobQueue.ActionsPerTurn - completed, cancellationToken);
                backlogged = batch.Turn.OldestDueTicks is not null;
                int work = batch.Turn.Completed + batch.Turn.SafetyChecks;
                advanced = work > 0;
                completed += work;
                lastMiner = batch.Miners.LastOrDefault() ?? lastMiner;
            } while (
                !cancellationToken.IsCancellationRequested
                && backlogged
                && advanced
                && completed < CapsuleerJobQueue.ActionsPerTurn
                && clock.GetElapsedTime(started) < budget
            );
            return lastMiner;
        }
        finally
        {
            // Shutdown still saves committed actions. A closing client does not need a fresh scene or Local roster.
            if (!cancellationToken.IsCancellationRequested)
            {
                long presentationStarted = clock.GetTimestamp();
                try
                {
                    Refresh();
                }
                finally
                {
                    long completed = clock.GetTimestamp();
                    updateDiagnostics.RecordUpdate(
                        new(clock.GetElapsedTime(started, completed), clock.GetElapsedTime(presentationStarted, completed))
                    );
                }
            }
        }
    }

    public void Refresh()
    {
        using var operation = operations?.Begin();
        using var ownership = synchronization.CurrentSystem is null
            ? synchronization.Enter("traffic presentation")
            : synchronization.TryEnterShared("traffic presentation");
        if (ownership is null)
        {
            // The last immutable presentation remains available. The maintenance
            // owner publishes the pending batch without delaying this player's frame.
            return;
        }
        var participatingSystems = handoff
            .PresentationSystems.Concat(_publishedPresentation?.Systems.Select(s => s.SystemId) ?? [])
            .Concat(_retryPresentation?.Miners.Select(m => m.Pilot.SystemId) ?? [])
            .Concat(_retryPresentation?.Haulers.Select(h => h.Pilot.SystemId) ?? [])
            .Distinct()
            .ToArray();
        using var systems = synchronization.TryEnterSystems("traffic presentation systems", participatingSystems);
        if (systems is null)
        {
            return;
        }
        {
            var batch = handoff.Capture();
            if (_retryPresentation is null && batch.Revision == _publishedPresentation?.Revision)
            {
                return;
            }
            if (_retryPresentation is { } retry)
            {
                batch = batch with
                {
                    Miners = retry.Miners.Concat(batch.Miners).GroupBy(m => m.Pilot.PilotId).Select(g => g.Last()).ToArray(),
                    Haulers = retry.Haulers.Concat(batch.Haulers).GroupBy(h => h.Pilot.PilotId).Select(g => g.Last()).ToArray(),
                };
            }
            _retryPresentation = batch;
            scene.Apply(batch);
            _presenceChanges.Clear();
            foreach (var observation in batch.Miners)
            {
                PublishPresence(observation.Pilot);
            }
            foreach (var observation in batch.Haulers)
            {
                PublishPresence(observation.Pilot);
            }
            if (_presenceChanges.Count > 0)
            {
                chat.PublishCapsuleers(_presenceChanges);
                foreach (var pilot in _presenceChanges)
                {
                    _lastPresence[pilot.Id] = pilot;
                }
            }
            _publishedPresentation = batch;
            _retryPresentation = null;
        }
    }

    private void PublishPresence(Eden.Gameplay.Docking.DockedPilot pilot)
    {
        var presence = new ChatPilot(
            pilot.PilotId,
            pilot.Name,
            pilot.CorpId,
            pilot.SystemId,
            (long)PilotRole.Player,
            pilot.Profile.GenderId,
            ChatRules.LocalKind(pilot.SystemId, pilot.RegionId)
        );
        if (presence != _lastPresence.GetValueOrDefault(pilot.PilotId))
        {
            _presenceChanges.Add(presence);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        var cancellation = Interlocked.Exchange(ref _cancellation, null);
        cancellation?.Cancel();
        _loop.GetAwaiter().GetResult();
        cancellation?.Dispose();
    }
}
