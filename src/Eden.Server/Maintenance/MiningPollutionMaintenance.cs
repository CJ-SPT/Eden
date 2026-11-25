using Eden.Gameplay.Mining;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class MiningPollutionMaintenance(IMiningPollutionClouds clouds, TimeProvider clock, IPilotOperationScope? operations = null)
    : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

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
        do
        {
            try
            {
                using var operation = operations?.Begin();
                clouds.Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Mining pollution maintenance failed; retrying next interval: {error.Message}");
            }
        } while (await timer.WaitForNextTickAsync().ConfigureAwait(false));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
