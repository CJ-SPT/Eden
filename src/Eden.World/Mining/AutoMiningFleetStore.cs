using Eden.Gameplay.Fleets;
using Eden.Gameplay.Mining;
using Eden.Infrastructure.Registration;

namespace Eden.World.Mining;

[Injectable(InjectionType.Singleton)]
internal sealed class AutoMiningFleetStore(ICooperativeFleets fleets, TimeProvider clock) : IAutoMiningFleets
{
    private sealed record Leader(long FleetId, AutoMiningDestination Destination, long Updated);

    private static readonly TimeSpan s_lifetime = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<long, Leader> _leaders = [];

    public long RequireLeader(long pilotId)
    {
        var identity = fleets.Identity(pilotId) ?? throw new InvalidDataException("Join a player fleet first.");
        return fleets.Read(pilotId, identity.FleetId).Members.Single(member => (member.Job & FleetRules.BossJob) != 0).Pilot.Id;
    }

    public void Publish(long pilotId, AutoMiningDestination destination)
    {
        var identity = fleets.Identity(pilotId);
        if (identity is null || (identity.Job & FleetRules.BossJob) == 0)
        {
            throw new InvalidDataException("Only the fleet boss can lead AutoMining.");
        }
        lock (_gate)
        {
            _leaders[pilotId] = new(identity.FleetId, destination, clock.GetTimestamp());
        }
    }

    public AutoMiningDestination Follow(long pilotId, long leaderId)
    {
        var identity = fleets.Identity(pilotId);
        var boss = fleets.Identity(leaderId);
        if (pilotId == leaderId || identity is null || boss?.FleetId != identity.FleetId || (boss.Job & FleetRules.BossJob) == 0)
        {
            throw new InvalidDataException("The AutoMining fleet boss changed. Start again to follow the new boss.");
        }
        lock (_gate)
        {
            if (
                !_leaders.TryGetValue(leaderId, out var leader)
                || leader.FleetId != identity?.FleetId
                || clock.GetElapsedTime(leader.Updated) >= s_lifetime
            )
            {
                throw new InvalidDataException("The fleet boss is not leading an active AutoMining run.");
            }
            return leader.Destination;
        }
    }

    public void Stop(long pilotId)
    {
        lock (_gate)
        {
            _leaders.Remove(pilotId);
        }
    }
}
