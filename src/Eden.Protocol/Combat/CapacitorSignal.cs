using Eden.Gameplay.Combat;
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
        if (cycle.Module.Kind == CapacitorModuleKind.Transfer)
        {
            return;
        }
        lock (_incoming)
        {
            var next = _incoming
                .GetValueOrDefault(cycle.TargetId, [])
                .Where(c => c.Module.ItemId != cycle.Module.ItemId)
                .Concat(active ? [cycle] : Array.Empty<CapacitorCycle>())
                .ToArray();
            if (next.Length == 0)
            {
                _incoming.Remove(cycle.TargetId);
            }
            else
            {
                _incoming[cycle.TargetId] = next;
            }
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
