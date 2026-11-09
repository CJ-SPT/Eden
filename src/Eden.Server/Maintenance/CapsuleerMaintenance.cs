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
    IPilotOperationScope? operations = null
) : IDisposable, ICapsuleerPresentation
{
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;
    private volatile bool _stopping;
    private readonly Dictionary<long, ChatPilot> _lastPresence = [];
    private CapsuleerBatch? _retryPresentation;
    private CapsuleerBatch? _publishedPresentation;

    internal void Start()
    {
        if (_timer is null)
        {
            _timer = new PeriodicTimer(CapsuleerJobQueue.WakeInterval, clock);
            _loop = Run(_timer);
        }
    }

    private async Task Run(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            try
            {
                bool backlogged;
                do
                {
                    Tick(out backlogged);
                    if (backlogged)
                    {
                        // A bounded turn releases ownership before another catch-up
                        // turn. The normal wake interval applies once work is current.
                        await Task.Yield();
                    }
                } while (backlogged && !_stopping);
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Capsuleer simulation failed: {error.Message}");
            }
        }
    }

    internal MinerObservation? Tick() => Tick(out _);

    private MinerObservation? Tick(out bool backlogged)
    {
        using var operation = operations?.Begin();
        long started = clock.GetTimestamp();
        try
        {
            MinerObservation? lastMiner = null;
            int completed = 0;
            do
            {
                // A job, its transaction and the queue revision are one atomic step.
                // Let waiting sessions enter before the next job or scene capture.
                using var owner = synchronization.Enter("traffic maintenance");
                var batch = scheduler.Run(scene.Stamp, maximumActions: 1);
                backlogged = batch.Turn.OldestDueTicks is not null;
                completed += batch.Turn.Completed;
                lastMiner = batch.Miners.LastOrDefault() ?? lastMiner;
            } while (
                backlogged
                && completed < CapsuleerJobQueue.ActionsPerTurn
                && clock.GetElapsedTime(started) < CapsuleerJobQueue.ExecutionBudget
            );
            return lastMiner;
        }
        finally
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

    public void Refresh()
    {
        using var operation = operations?.Begin();
        using (synchronization.Enter("traffic presentation"))
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
            foreach (var observation in batch.Miners)
            {
                PublishPresence(observation.Pilot);
            }
            foreach (var observation in batch.Haulers)
            {
                PublishPresence(observation.Pilot);
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
            chat.PublishCapsuleer(presence);
            _lastPresence[pilot.PilotId] = presence;
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
