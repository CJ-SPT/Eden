using Eden.Gameplay.Capsuleers;
using Eden.World.Catalog;
using Eden.World.Configuration;

namespace Eden.World.Capsuleers;

internal sealed partial class CapsuleerScheduler
{
    private readonly WorldConfigurationSlot _populationSettings = WorldConfigurationSlot.For(database.Path);
    private readonly HashSet<long> _preparedInbound = [];
    private readonly Dictionary<long, InboundPreparation> _inboundPreparation = [];

    internal sealed record InboundPreparation(WatchedTrafficGate Gate, bool BothObserved, int Visitor = 0);

    internal InboundPreparation? NextInboundPreparation()
    {
        using var owner = synchronization.Enter("inbound preparation request");
        var occupied = presence.Read().ActiveSystems;
        return _inboundPreparation.Values.FirstOrDefault(request =>
            occupied.Contains(request.Gate.SourceSystemId) && occupied.Contains(request.Gate.ObserverSystemId)
        );
    }

    internal void InboundPrepared(InboundPreparation request, long? pilot)
    {
        using var owner = synchronization.Enter("inbound preparation publication");
        long key = GateKey(request.Gate);
        if (_inboundPreparation.GetValueOrDefault(key) != request)
        {
            return;
        }
        if (pilot is long committed && presence.Read().ActiveSystems.Contains(request.Gate.SourceSystemId))
        {
            Load(request.Gate.SourceSystemId, committed);
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

    private void ReconcileInbound(IReadOnlyList<WatchedTrafficGate> gates)
    {
        foreach (long key in _inboundPreparation.Keys.Except(gates.Select(GateKey)).ToArray())
        {
            _inboundPreparation.Remove(key);
        }
        foreach (var gate in gates)
        {
            long key = GateKey(gate);
            if (_preparedInbound.Contains(key))
            {
                continue;
            }
            if (Live is not null)
            {
                if (_inboundPreparation.TryAdd(key, new(gate, BothObserved: true)))
                {
                    presence.RequestPreparation();
                }
                continue;
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            if (CapsuleerTrafficRoutes.Inbound(catalog, gate, sourceObserved: true)?.Systems[0] == gate.SourceSystemId)
            {
                using CapsuleerProvisioner provisioner = new(database, clock);
                for (int visitor = 0; visitor < _populationSettings.Settings.Capsuleers.GateVisitors; visitor++)
                {
                    if (provisioner.EnsureInbound(gate, sourceObserved: true, visitor: visitor) is not long pilot)
                    {
                        break;
                    }
                    Load(gate.SourceSystemId, pilot);
                }
            }
            _preparedInbound.Add(key);
        }
    }

    private static long GateKey(WatchedTrafficGate gate) => Math.Min(gate.ArrivalGateId, gate.DepartureGateId);
}
