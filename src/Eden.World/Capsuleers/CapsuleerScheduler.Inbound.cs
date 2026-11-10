using Eden.Gameplay.Capsuleers;
using Eden.World.Configuration;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly WorldConfigurationSlot _populationSettings = WorldConfigurationSlot.For(database.Path);
    private readonly Dictionary<long, long> _inboundLanes = [];
    private readonly HashSet<(long Gate, bool BothSystemsObserved)> _deferredInbound = [];
    private readonly HashSet<long> _travelers = [];

    internal sealed record InboundPreparation(WatchedTrafficGate Gate, bool BothObserved, int Visitor = 0);

    private readonly Dictionary<long, InboundPreparation> _inboundPreparation = [];

    internal InboundPreparation? NextInboundPreparation()
    {
        using var owner = synchronization.Enter("inbound preparation request");
        return _inboundPreparation.Values.FirstOrDefault();
    }

    internal void InboundPrepared(InboundPreparation request, long? pilot)
    {
        using var owner = synchronization.Enter("inbound preparation publication");
        _membershipDirty = true;
        long key = Math.Min(request.Gate.ArrivalGateId, request.Gate.DepartureGateId);
        if (pilot is long committed)
        {
            Load(preparedPilot: committed);
            _travelers.Add(committed);
        }
        if (pilot is not null && request.Visitor + 1 < _populationSettings.Settings.Capsuleers.GateVisitors)
        {
            _inboundPreparation[key] = request with { Visitor = request.Visitor + 1 };
        }
        else
        {
            _inboundPreparation.Remove(key);
            if (pilot is long last)
            {
                _inboundLanes[key] = last;
            }
            else
            {
                _deferredInbound.Add((key, request.BothObserved));
            }
        }
        presence.Wake();
    }

    private HashSet<long> ReconcileInbound(HumanActivitySnapshot activity, IReadOnlyList<WatchedTrafficGate> gates)
    {
        var policy = _populationSettings.Settings.Capsuleers;
        foreach (var gate in gates)
        {
            long key = Math.Min(gate.ArrivalGateId, gate.DepartureGateId);
            bool bothObserved = activity.ActiveSystems.Contains(gate.SourceSystemId);
            if (_inboundLanes.ContainsKey(key) || _deferredInbound.Contains((key, bothObserved)))
            {
                continue;
            }
            if (Live is not null)
            {
                if (_inboundPreparation.TryAdd(key, new(gate, bothObserved)))
                {
                    presence.RequestPreparation();
                }
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
