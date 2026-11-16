using Eden.Gameplay.Alliances;
using Eden.Gameplay.Corporations;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CorporationMaintenance(
    ICorporationVoting voting,
    ICorporationMemberRemovals removals,
    TimeProvider clock,
    IAllianceMembership alliances
) : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

    internal void Start()
    {
        if (_timer is null)
        {
            voting.Advance();
            removals.Advance();
            alliances.Advance();
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
                voting.Advance();
                removals.Advance();
                alliances.Advance();
            }
            catch (Exception error)
            {
                ServerLog.Console.Error($"Corporation maintenance failed; retrying next interval: {error.Message}");
            }
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
