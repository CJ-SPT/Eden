using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Server.Chat;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMaintenance(ICapsuleerScheduler scheduler, ICapsuleerScene scene, ChatRuntime chat, TimeProvider clock)
    : IDisposable
{
    private readonly Lock _gate = new();
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;
    private readonly Dictionary<long, ChatPilot> _lastPresence = [];

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
                Tick();
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Capsuleer simulation failed: {error.Message}");
            }
        }
    }

    internal MinerObservation? Tick()
    {
        lock (_gate)
        {
            var batch = scheduler.Run(scene.Stamp);
            foreach (var observation in batch.Miners)
            {
                scene.Publish(observation);
                PublishPresence(observation.Pilot);
            }
            foreach (var delivery in batch.Haulers)
            {
                scene.Publish(delivery);
                PublishPresence(delivery.Pilot);
            }
            return batch.Miners.LastOrDefault();
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
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
