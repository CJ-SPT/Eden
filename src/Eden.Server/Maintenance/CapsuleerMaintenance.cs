using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Server.Chat;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMaintenance(
    ICapsuleerSimulation simulation,
    ICapsuleerScene scene,
    ChatRuntime chat,
    TimeProvider clock,
    ICapsuleerHauling hauling,
    IHumanPresence presence
) : IDisposable
{
    private readonly Lock _gate = new();
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;
    private long _lastSecond = presence.Read().ElapsedTicks / TimeSpan.TicksPerSecond;
    private readonly Dictionary<long, ChatPilot> _lastPresence = [];

    internal void Start()
    {
        if (_timer is null)
        {
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
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
            var activity = presence.Read();
            long second = activity.ElapsedTicks / TimeSpan.TicksPerSecond;
            if (!activity.Active || second <= _lastSecond)
            {
                return null;
            }
            _lastSecond = second;
            var observation = simulation.Advance(scene.Stamp);
            if (observation is not null)
            {
                scene.Publish(observation);
                PublishPresence(observation.Pilot);
            }
            if (hauling.Advance(scene.Stamp) is { } delivery)
            {
                scene.Publish(delivery);
                PublishPresence(delivery.Pilot);
            }
            return observation;
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
