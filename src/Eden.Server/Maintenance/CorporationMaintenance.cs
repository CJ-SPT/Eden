using Eden.Gameplay.Alliances;
using Eden.Gameplay.Capsuleers;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.FactionWarfare;
using Eden.Infrastructure.Diagnostics;
using Eden.Infrastructure.Operations;
using Eden.Infrastructure.Registration;

namespace Eden.Server.Maintenance;

[Injectable(InjectionType.Singleton)]
internal sealed class CorporationMaintenance(
    ICorporationVoting voting,
    ICorporationMemberRemovals removals,
    TimeProvider clock,
    IAllianceMembership alliances,
    ICapsuleerAlliancePlanning capsuleerAlliances,
    IMilitiaOrganizations militia,
    ICorporationWarMaintenance wars,
    IPilotOperationScope? operations = null
) : IDisposable
{
    private Task _loop = Task.CompletedTask;
    private PeriodicTimer? _timer;

    internal void Start()
    {
        if (_timer is null)
        {
            Tick();
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
                ServerLog.Console.Error($"Corporation maintenance failed; retrying next interval: {error.Message}");
            }
        }
    }

    internal void Tick()
    {
        using var operation = operations?.Begin();
        voting.Advance();
        removals.Advance();
        militia.Advance();
        alliances.Advance();
        capsuleerAlliances.Advance();
        wars.Advance(clock.GetUtcNow().UtcDateTime.ToFileTimeUtc());
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _loop.GetAwaiter().GetResult();
    }
}
