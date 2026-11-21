using Eden.Data.World.Capsuleers;
using Eden.Gameplay.Capsuleers;
using Eden.World.Configuration;
using Eden.World.Runtime.Economy;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly WorldConfigurationSlot _populationSettings = WorldConfigurationSlot.For(database.Path);
    private readonly HashSet<long> _preparedInbound = [];
    private readonly Dictionary<long, InboundPreparation> _inboundPreparation = [];

    private void RegisterVisitor(long pilot)
    {
        WorldActionScope.Inspect(database, world => Register(world, [world.Set<SavedCapsuleerAgent>().Find(pilot)!]));
        _membershipDirty = true;
    }

    internal sealed record InboundPreparation(WatchedTrafficGate Gate, bool BothObserved, int Visitor = 0);

    internal InboundPreparation? NextInboundPreparation() => InboundPreparations().FirstOrDefault();

    internal IReadOnlyList<InboundPreparation> InboundPreparations()
    {
        using var owner = synchronization.Enter("inbound preparation request");
        var occupied = presence.Read().ActiveSystems;
        return _inboundPreparation.Values.Where(request => occupied.Contains(request.Gate.ObserverSystemId)).ToArray();
    }

    internal void InboundPrepared(InboundPreparation request, long? pilot)
    {
        using var owner = synchronization.Enter("inbound preparation publication");
        long key = GateKey(request.Gate);
        if (_inboundPreparation.GetValueOrDefault(key) != request)
        {
            return;
        }
        var occupied = presence.Read().ActiveSystems;
        if (!occupied.Contains(request.Gate.ObserverSystemId))
        {
            // Losing an observer during planning is not an unsupported lane. Retain
            // the cursor so a later observation can reuse any committed founder.
            if (pilot is not null)
            {
                presence.Wake();
            }
            return;
        }
        if (pilot is not null)
        {
            // The committed resident roster admits a visitor locally or paces its offscreen start.
            Load();
            if (request.Visitor + 1 < _populationSettings.Settings.Capsuleers.GateVisitors)
            {
                _inboundPreparation[key] = request with { Visitor = request.Visitor + 1 };
                presence.Wake();
                return;
            }
        }
        _inboundPreparation.Remove(key);
        _preparedInbound.Add(key);
        presence.Wake();
    }

    private void ReconcileInbound(IReadOnlyList<WatchedTrafficGate> gates, IReadOnlyList<int> occupied)
    {
        foreach (long key in _inboundPreparation.Keys.Except(gates.Select(GateKey)).ToArray())
        {
            _inboundPreparation.Remove(key);
        }
        foreach (var gate in gates.DistinctBy(GateKey))
        {
            long key = GateKey(gate);
            if (_preparedInbound.Contains(key))
            {
                continue;
            }
            if (Live is not null)
            {
                var request = _inboundPreparation.GetValueOrDefault(key);
                var watched = request is not null && gates.Contains(request.Gate) ? request.Gate : gate;
                InboundPreparation next = new(watched, occupied.Contains(watched.SourceSystemId), request?.Visitor ?? 0);
                if (request != next)
                {
                    _inboundPreparation[key] = next;
                    presence.RequestPreparation();
                }
                continue;
            }
            bool sourceObserved = occupied.Contains(gate.SourceSystemId);
            using CapsuleerProvisioner provisioner = new(database, clock);
            for (int visitor = 0; visitor < _populationSettings.Settings.Capsuleers.GateVisitors; visitor++)
            {
                if (provisioner.EnsureInbound(gate, sourceObserved, visitor) is not long pilot)
                {
                    break;
                }
                RegisterVisitor(pilot);
            }
            _preparedInbound.Add(key);
        }
    }

    private static long GateKey(WatchedTrafficGate gate) => Math.Min(gate.ArrivalGateId, gate.DepartureGateId);
}
