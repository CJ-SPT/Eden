using Eden.Gameplay.Capsuleers;
using Eden.World.Fittings;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private void SettleReadySystems(HumanActivitySnapshot activity)
    {
        foreach (var state in _systems.Observed())
        {
            if (
                !(_systemAgents.GetValueOrDefault(state.SystemId) ?? []).Any(p =>
                    _running.Contains(p) && ScheduledJob(p).DueTicks <= activity.ElapsedTicks
                )
            )
            {
                _systems.Settle(state.SystemId, Math.Max(state.SettledTicks, activity.ElapsedTicks));
            }
        }
    }

    private void UpdateDetailRetention(HumanActivitySnapshot activity, IReadOnlyList<SystemSimulationState> transitions)
    {
        ResidentFitCache.Find(database.Path)?.Pin(activity.Pilots.Select(p => p.PilotId).Concat(_running));
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
