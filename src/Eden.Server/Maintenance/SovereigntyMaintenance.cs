using Eden.Gameplay.Sovereignty;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class SovereigntyMaintenance(TimeProvider clock, ISovereigntyResources resources, IPilotOperationScope? operations = null)
    : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

    internal void Start()
    {
        if (_timer is null)
        {
            using var operation = operations?.Begin();
            resources.AdvanceAll();
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
                resources.AdvanceAll();
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Sovereignty maintenance failed; retrying next interval: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
