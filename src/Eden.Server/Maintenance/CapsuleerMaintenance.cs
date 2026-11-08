using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Chat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.Server.Chat;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CapsuleerMaintenance(ICapsuleerSimulation simulation, ICapsuleerScene scene, ChatRuntime chat, TimeProvider clock)
    : IDisposable
{
    private readonly Lock _gate = new();
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;
    private long _lastSecond;
    private ChatPilot? _lastPresence;

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
            var (active, elapsed) = chat.HumanActivity();
            long second = elapsed.Ticks / TimeSpan.TicksPerSecond;
            if (!active || second <= _lastSecond)
            {
                return null;
            }
            _lastSecond = second;
            if (simulation.Advance(scene.Stamp) is not { } observation)
            {
                return null;
            }
            scene.Publish(observation);
            var pilot = observation.Pilot;
            var presence = new ChatPilot(
                pilot.PilotId,
                pilot.Name,
                pilot.CorpId,
                pilot.SystemId,
                (long)PilotRole.Player,
                pilot.Profile.GenderId,
                ChatRules.LocalKind(pilot.SystemId, pilot.RegionId)
            );
            if (presence != _lastPresence)
            {
                chat.PublishCapsuleer(presence);
                _lastPresence = presence;
            }
            return observation;
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
