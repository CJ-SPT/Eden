using Eden.Gameplay.Capsuleers;
using Eden.World.Configuration;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly WorldConfigurationSlot _populationSettings = WorldConfigurationSlot.For(database.Path);
    private readonly Dictionary<long, long> _inboundLanes = [];
    private readonly HashSet<(long Gate, bool BothSystemsObserved)> _deferredInbound = [];
    private readonly HashSet<long> _travelers = [];

    private HashSet<long> ReconcileInbound(HumanActivitySnapshot activity)
    {
        var policy = _populationSettings.Settings.Capsuleers;
        IReadOnlyList<WatchedTrafficGate> gates = policy.Enabled && policy.GateVisitors > 0 ? gateWatch.Read(activity) : [];
        foreach (var gate in gates)
        {
            long key = Math.Min(gate.ArrivalGateId, gate.DepartureGateId);
            bool bothObserved = activity.ActiveSystems.Contains(gate.SourceSystemId);
            if (_inboundLanes.ContainsKey(key) || _deferredInbound.Contains((key, bothObserved)))
            {
                continue;
            }
            long? lastPilot = null;
            for (int visitor = 0; visitor < policy.GateVisitors; visitor++)
            {
                var prepared = new CapsuleerProvisioner(database, clock).EnsureInbound(gate, bothObserved, visitor);
                if (prepared is not long pilot)
                {
                    _deferredInbound.Add((key, bothObserved));
                    break;
                }
                if (!_haulers.ContainsKey(pilot))
                {
                    Load(preparedPilot: pilot);
                }
                _travelers.Add(pilot);
                lastPilot = pilot;
            }
            if (lastPilot is { } preparedPilot)
            {
                _inboundLanes[key] = preparedPilot;
            }
        }
        // Gate watches introduce bounded station journeys. Existing journeys keep their
        // direction and deadlines independently of which side is subsequently watched.
        return _travelers
            .Where(pilot =>
                !_suspended.Contains(pilot) && (!_haulers[pilot].IsRatting || activity.ActiveSystems.Contains(_locations[pilot]))
            )
            .ToHashSet();
    }
}
