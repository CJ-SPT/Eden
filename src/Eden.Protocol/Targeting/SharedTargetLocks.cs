using Eden.Infrastructure.Registration;

namespace Eden.Protocol.Targeting;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedTargetLocks
{
    private readonly Lock _gate = new();
    private readonly HashSet<(long Ship, long Target)> _locks = [];
    private readonly HashSet<(long Ship, long Target)> _silent = [];

    public void Add(long ship, long target, bool silent = false)
    {
        lock (_gate)
        {
            _locks.Add((ship, target));
            if (silent)
            {
                _silent.Add((ship, target));
            }
        }
    }

    public bool Contains(long ship, long target)
    {
        lock (_gate)
        {
            return _locks.Contains((ship, target));
        }
    }

    internal void Reveal(long ship, long target)
    {
        lock (_gate)
        {
            _silent.Remove((ship, target));
        }
    }

    public void Remove(long ship, long target)
    {
        lock (_gate)
        {
            _locks.Remove((ship, target));
            _silent.Remove((ship, target));
        }
    }

    public void RemoveShip(long ship)
    {
        lock (_gate)
        {
            _locks.RemoveWhere(edge => edge.Ship == ship || edge.Target == ship);
            _silent.RemoveWhere(edge => edge.Ship == ship || edge.Target == ship);
        }
    }

    public long[] Targeters(long target)
    {
        lock (_gate)
        {
            return _locks.Where(edge => edge.Target == target && !_silent.Contains(edge)).Select(edge => edge.Ship).Order().ToArray();
        }
    }
}
