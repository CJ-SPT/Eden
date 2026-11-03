namespace Eden.Protocol.Targeting;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedTargetLocks
{
    private readonly Lock _gate = new();
    private readonly HashSet<(long Ship, long Target)> _locks = [];

    public void Add(long ship, long target)
    {
        lock (_gate)
        {
            _locks.Add((ship, target));
        }
    }

    public bool Contains(long ship, long target)
    {
        lock (_gate)
        {
            return _locks.Contains((ship, target));
        }
    }

    public void Remove(long ship, long target)
    {
        lock (_gate)
        {
            _locks.Remove((ship, target));
        }
    }

    public void RemoveShip(long ship)
    {
        lock (_gate)
        {
            _locks.RemoveWhere(edge => edge.Ship == ship || edge.Target == ship);
        }
    }

    public long[] Targeters(long target)
    {
        lock (_gate)
        {
            return _locks.Where(edge => edge.Target == target).Select(edge => edge.Ship).Order().ToArray();
        }
    }
}
