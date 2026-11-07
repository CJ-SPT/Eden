using Eden.Gameplay.Ammunition;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Ammunition;
using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Docking;

// Transfer replies and ItemGetInfo share this connection-owned staging queue.
[Injectable(InjectionType.Scoped)]
internal sealed class DeferredRefitPrimes(MagazineWire magazines, IPilotAmmunition ammunition)
{
    private long _ship;
    private readonly HashSet<long> _modules = [];
    private readonly Dictionary<long, MagazineChange> _changes = [];

    internal void Clear()
    {
        _ship = 0;
        _modules.Clear();
        _changes.Clear();
    }

    internal void Defer(long ship, IEnumerable<long> modules, IEnumerable<MagazineChange> changes)
    {
        if (_ship != ship)
        {
            _modules.Clear();
            _changes.Clear();
            _ship = ship;
        }
        _modules.UnionWith(modules);
        foreach (var change in changes)
        {
            _changes[change.Current!.ModuleId] = change;
        }
    }

    internal void Primed(PilotCallContext context, long module)
    {
        if (_ship != context.Pilot.ShipId)
        {
            _modules.Clear();
            _changes.Clear();
            return;
        }
        if (_modules.Remove(module) && _modules.Count == 0)
        {
            var current = ammunition.Read(context.Pilot.PilotId, _ship).ToDictionary(m => m.ModuleId);
            var changes = _changes.Keys.Where(current.ContainsKey).Select(id => new MagazineChange(null, current[id])).ToArray();
            magazines.NotifyChanges(context, changes);
            _changes.Clear();
        }
    }
}
