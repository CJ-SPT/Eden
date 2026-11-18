using Eden.Gameplay.Capsuleers;
using Eden.World.Fittings;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly HashSet<long> _savedLocalSorties = [];

    private bool CanRunUnobserved(long pilot)
    {
        if (!_haulers.ContainsKey(pilot))
        {
            return false;
        }
        if (Live?.HaulerCheckpoints.TryGetValue(pilot, out var checkpoint) == true)
        {
            return !LocalSortie(checkpoint.Work) || ActiveOperation(pilot);
        }
        return !_savedLocalSorties.Contains(pilot);
    }

    private static bool LocalSortie(HaulerProgress? work) => work?.Ratting is { Patrol: null };

    private void SettleReadySystems(HumanActivitySnapshot activity)
    {
        foreach (var state in _systems.Observed())
        {
            long due = ReadyDeadline(state.SystemId);
            if (due > activity.ElapsedTicks)
            {
                _systems.Settle(state.SystemId, Math.Max(state.SettledTicks, activity.ElapsedTicks));
            }
        }
    }

    private sealed record SystemReadiness(Eden.World.Runtime.LivePresentationVersion Version, long Membership, long DueTicks);

    private readonly Dictionary<int, SystemReadiness> _readiness = [];

    private long ReadyDeadline(int system)
    {
        var version = Live?.PresentationVersion(system);
        if (
            version is { } current
            && _readiness.TryGetValue(system, out var previous)
            && previous.Version == current
            && previous.Membership == Reconciliations
        )
        {
            return previous.DueTicks;
        }
        long due = long.MaxValue;
        foreach (long pilot in _systemAgents.GetValueOrDefault(system) ?? [])
        {
            if (_running.Contains(pilot))
            {
                due = Math.Min(due, ScheduledJob(pilot).DueTicks);
            }
        }
        if (version is { } captured)
        {
            _readiness[system] = new(captured, Reconciliations, due);
        }
        return due;
    }

    private void UpdateDetailRetention(HumanActivitySnapshot activity, IReadOnlyList<SystemSimulationState> transitions)
    {
        var observed = activity.ActiveSystems.SelectMany(system => _systemAgents.GetValueOrDefault(system) ?? []);
        ResidentFitCache.Find(database.Path)?.Pin(activity.Pilots.Select(p => p.PilotId).Concat(observed.Where(_running.Contains)));
        if (Live is not { } live)
        {
            return;
        }
        live.PublishDetailedSystems(activity.ActiveSystems);
        foreach (var state in transitions)
        {
            if (state.Mode == SystemSimulationMode.Deactivating)
            {
                live.ReleaseSystemDetails(state.SystemId);
            }
        }
    }
}
