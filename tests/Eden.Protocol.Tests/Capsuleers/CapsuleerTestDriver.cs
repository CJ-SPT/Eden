using System.Runtime.CompilerServices;
using Eden.Gameplay.Capsuleers;
using Microsoft.Extensions.DependencyInjection;

namespace Eden.Protocol.Tests.Capsuleers;

// Legacy role fixtures now drive the real queue and its human-active clock. They
// never invoke a private role state machine or bypass due/revision checks.
internal sealed class CapsuleerTestDriver
{
    private static readonly ConditionalWeakTable<ServiceProvider, CapsuleerTestDriver> Drivers = new();
    private readonly ServiceProvider _host;
    private readonly Guid _observer = Guid.NewGuid();
    private readonly TestClock _clock;
    private readonly ICapsuleerScheduler _scheduler;
    private readonly IHumanPresence _presence;
    private readonly IReadOnlyList<CapsuleerIdentity> _pilots;

    private CapsuleerTestDriver(ServiceProvider host, TestClock clock)
    {
        _host = host;
        _clock = clock;
        _scheduler = host.GetRequiredService<ICapsuleerScheduler>();
        _presence = host.GetRequiredService<IHumanPresence>();
        _pilots = host.GetRequiredService<ICapsuleerCommunity>().Pilots();
        if (!_presence.Read().Active)
        {
            _presence.Update(Guid.NewGuid(), 30000141);
        }
    }

    internal static MinerObservation Miner(ServiceProvider host, TestClock clock) =>
        Drivers.GetValue(host, h => new(h, clock)).AdvanceMiner();

    internal static HaulerObservation Hauler(ServiceProvider host, TestClock clock) =>
        Drivers.GetValue(host, h => new(h, clock)).AdvanceHauler();

    private long Advance(CapsuleerCareer career)
    {
        long pilot = _pilots.Single(p => p.Career == career).PilotId;
        int system = career == CapsuleerCareer.Mining ? _scheduler.Miner(pilot)!.Pilot.SystemId : _scheduler.Hauler(pilot)!.Pilot.SystemId;
        if (!_presence.Read().ActiveSystems.Contains(system))
        {
            _presence.Update(_observer, system);
        }
        var job = _scheduler.Jobs().Single(j => j.PilotId == pilot);
        long elapsed = _presence.Read().ElapsedTicks;
        if (elapsed < job.DueTicks)
        {
            _clock.Milliseconds += (long)Math.Ceiling((double)(job.DueTicks - elapsed) / TimeSpan.TicksPerMillisecond);
        }
        for (int batch = 0; batch < 10; batch++)
        {
            _host.GetRequiredService<Eden.Server.Maintenance.CapsuleerMaintenance>().Tick();
            if (_scheduler.Jobs().Single(j => j.PilotId == pilot).Revision > job.Revision)
            {
                return pilot;
            }
        }
        throw new InvalidOperationException("The due role was not serviced by the bounded queue.");
    }

    private MinerObservation AdvanceMiner() => _scheduler.Miner(Advance(CapsuleerCareer.Mining))!;

    private HaulerObservation AdvanceHauler() => _scheduler.Hauler(Advance(CapsuleerCareer.Hauling))!;
}
