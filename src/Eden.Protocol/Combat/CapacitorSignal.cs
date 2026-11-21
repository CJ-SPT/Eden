using Eden.Gameplay.Combat;
using Eden.Gameplay.Propulsion;
using Eden.Infrastructure.Registration;

namespace Eden.Protocol.Combat;

[Injectable(InjectionType.Singleton)]
internal sealed class CapacitorSignal : ICapacitorEvents
{
    private readonly Dictionary<long, CapacitorCycle[]> _incoming = [];
    internal event Action<CapacitorChanged>? Changed;

    public void Publish(CapacitorChanged change) => Changed?.Invoke(change);

    internal void Set(CapacitorCycle cycle, bool active)
    {
        if (cycle.Module.Kind == CapacitorModuleKind.Transfer || cycle.TargetKind == TackleTargetKind.Npc)
        {
            return;
        }
        lock (_incoming)
        {
            Set(_incoming, cycle.TargetId, cycle, active);
        }
    }

    private static void Set<TKey>(Dictionary<TKey, CapacitorCycle[]> index, TKey key, CapacitorCycle cycle, bool active)
        where TKey : notnull
    {
        // An old gunner can disconnect after another session starts a new cycle on the same module.
        var next = index
            .GetValueOrDefault(key, [])
            .Where(c => c.Module.ItemId != cycle.Module.ItemId || !active && c != cycle)
            .Concat(active ? [cycle] : Array.Empty<CapacitorCycle>())
            .ToArray();
        if (next.Length == 0)
        {
            index.Remove(key);
        }
        else
        {
            index[key] = next;
        }
    }

    internal IReadOnlyList<CapacitorCycle> Incoming(long ship, long now)
    {
        lock (_incoming)
        {
            if (!_incoming.TryGetValue(ship, out var current))
            {
                return Array.Empty<CapacitorCycle>();
            }
            if (current.Any(c => c.ExpiresAt <= now))
            {
                current = current.Where(c => c.ExpiresAt > now).ToArray();
                if (current.Length == 0)
                {
                    _incoming.Remove(ship);
                }
                else
                {
                    _incoming[ship] = current;
                }
            }
            return current;
        }
    }
}
