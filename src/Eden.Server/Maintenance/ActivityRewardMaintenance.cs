using Eden.Gameplay.Combat;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class ActivityRewardMaintenance(IActivityRewardTicks rewards, TimeProvider clock) : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

    internal void Start()
    {
        if (_timer is null)
        {
            Advance();
            _timer = new PeriodicTimer(TimeSpan.FromSeconds(1), clock);
            _loop = Run(_timer);
        }
    }

    private async Task Run(PeriodicTimer timer)
    {
        while (await timer.WaitForNextTickAsync().ConfigureAwait(false))
        {
            Advance();
        }
    }

    private void Advance()
    {
        try
        {
            rewards.Advance();
        }
        catch (Exception error)
        {
            ServerLog.Console.Error($"Activity reward settlement failed and will retry: {error}");
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
