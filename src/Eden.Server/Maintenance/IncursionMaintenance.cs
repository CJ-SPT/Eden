using Eden.Gameplay.Exploration;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class IncursionMaintenance(IIncursionMaintenance incursions, TimeProvider clock, IPilotOperationScope? operations = null)
    : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

    internal void Start()
    {
        if (_timer is null)
        {
            using var operation = operations?.Begin();
            incursions.Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            _timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
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
                incursions.Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Incursion maintenance failed; retrying next interval: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
