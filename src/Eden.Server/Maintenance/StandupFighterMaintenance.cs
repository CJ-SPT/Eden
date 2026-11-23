using Eden.Gameplay.Flight;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class StandupFighterMaintenance(IStandupFighterFlight fighters, TimeProvider clock, IPilotOperationScope? operations = null)
    : IDisposable
{
    private PeriodicTimer? _timer;
    private Task _loop = Task.CompletedTask;

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
                using var operation = operations?.Begin();
                fighters.AdvanceReturns(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Standup fighter recovery failed; retrying next interval: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
