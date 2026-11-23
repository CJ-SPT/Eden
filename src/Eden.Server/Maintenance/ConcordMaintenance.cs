using Eden.Gameplay.Combat;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class ConcordMaintenance(
    IConcord concord,
    TimeProvider clock,
    IWorldSynchronization synchronization,
    IMilitiaNavy? militiaNavy = null,
    IMilitiaHubs? militiaHubs = null,
    IMilitiaComplexSimulation? militiaComplexes = null,
    IPilotOperationScope? operations = null
) : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

    internal void Start()
    {
        if (_timer is null)
        {
            using var operation = operations?.Begin();
            militiaHubs?.Sweep(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            _loop = Run(_timer);
        }
    }

    internal void Tick()
    {
        using var operation = operations?.Begin();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        using (synchronization.Enter("Empire enforcement"))
        {
            concord.Sweep(now);
            militiaNavy?.Sweep(now);
            militiaHubs?.Sweep(now);
        }
        militiaComplexes?.Tick(now);
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
                ServerLog.Console.Error($"Empire enforcement failed; retrying next interval: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
