using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly HashSet<int> _remoteSystems = [];
    private readonly HashSet<int> _freshRemote = [];
    private readonly Dictionary<long, int> _communities = [];
    private readonly HashSet<int> _detailedSystems = [];
    private readonly HashSet<int> _combatPinnedSystems = [];
    private readonly HashSet<long> _detailedPilots = [];
    private readonly HashSet<long> _detailCandidates = [];

    private void StartRemote(int system, long elapsed)
    {
        if (Live is null || !_remoteSystems.Add(system))
        {
            return;
        }
        try
        {
            using WorldDbContext world = new(database.Path) { Participants = TransactionParticipants.None };
            world.Database.ExecuteSql($"INSERT OR IGNORE INTO WorldCapsuleerSimulation(SystemId,StartedTicks) VALUES({system},{elapsed})");
            _systems.Register(system);
            _freshRemote.Add(system);
        }
        catch
        {
            _remoteSystems.Remove(system);
            throw;
        }
    }

    private bool Detailed(long pilot, HumanActivitySnapshot activity) =>
        activity.ActiveSystems.Contains(_locations[pilot])
        || _combatPinnedSystems.Contains(_locations[pilot])
        || _miners.GetValueOrDefault(pilot)?.RequiresDetail == true
        || _haulers.GetValueOrDefault(pilot)?.RequiresDetail == true;

    private void SettleReadySystems(HumanActivitySnapshot activity)
    {
        foreach (var state in _systems.Observed())
        {
            if (
                !(_systemAgents.GetValueOrDefault(state.SystemId) ?? []).Any(p =>
                    _running.Contains(p) && _locations[p] == state.SystemId && Current[p].DueTicks <= activity.ElapsedTicks
                )
            )
            {
                _systems.Settle(state.SystemId, Math.Max(state.SettledTicks, activity.ElapsedTicks));
            }
        }
    }

    private void ReleaseRemoteDetails(HumanActivitySnapshot activity, bool membershipChanged = false)
    {
        using var phase = Eden.Infrastructure.Diagnostics.SessionPerformance.Phase("traffic.detailRetention");
        foreach (int system in activity.ActiveSystems)
        {
            _detailedSystems.Add(system);
        }
        if (membershipChanged)
        {
            _detailCandidates.UnionWith(_running);
            _detailedPilots.IntersectWith(_running);
        }
        bool pinsChanged = membershipChanged;
        foreach (long pilot in _detailCandidates)
        {
            bool detailed = Detailed(pilot, activity);
            _miners.GetValueOrDefault(pilot)?.SetDetailed(detailed);
            _haulers.GetValueOrDefault(pilot)?.SetDetailed(detailed);
            if (detailed)
            {
                pinsChanged |= _detailedPilots.Add(pilot);
                _detailedSystems.Add(_locations[pilot]);
            }
            else
            {
                pinsChanged |= _detailedPilots.Remove(pilot);
            }
        }
        _detailCandidates.Clear();
        var pinned = _detailedPilots
            .Where(p => _miners.GetValueOrDefault(p)?.RequiresDetail == true || _haulers.GetValueOrDefault(p)?.RequiresDetail == true)
            .Select(p => _locations[p])
            .ToHashSet();
        if (pinsChanged)
        {
            ResidentFitCache.Find(database.Path)?.Pin(activity.Pilots.Select(p => p.PilotId).Concat(_detailedPilots));
        }
        foreach (int system in _detailedSystems.Except(activity.ActiveSystems).ToArray())
        {
            bool holds = pinned.Contains(system) || Live?.ReleaseSystemDetails(system) == false;
            if (holds)
            {
                _combatPinnedSystems.Add(system);
            }
            else
            {
                _combatPinnedSystems.Remove(system);
            }
            _systems.Pin(system, holds);
            if (!holds)
            {
                _detailedSystems.Remove(system);
            }
        }
        Live?.PublishDetailedSystems(activity.ActiveSystems.Concat(pinned).Concat(_combatPinnedSystems));
    }
}
