using Eden.Gameplay.Targeting;
using Eden.Infrastructure.Registration;

namespace Eden.Protocol.Targeting;

[Injectable(InjectionType.Singleton)]
internal sealed class SharedTargetLocks : IPilotTargetLocks, IPilotLockBreaks
{
    private readonly Lock _gate = new();
    private readonly HashSet<(long Ship, long Target)> _locks = [];
    private readonly HashSet<(long Ship, long Target)> _silent = [];
    private readonly HashSet<(long Ship, long Target)> _pending = [];
    private readonly Dictionary<long, int> _outgoing = [];
    private readonly Dictionary<long, long> _breaks = [];

    public void BreakOutgoing(long ship)
    {
        lock (_gate)
        {
            _locks.RemoveWhere(edge => edge.Ship == ship);
            _pending.RemoveWhere(edge => edge.Ship == ship);
            _outgoing.Remove(ship);
            _silent.RemoveWhere(edge => edge.Ship == ship);
            _breaks[ship] = checked(_breaks.GetValueOrDefault(ship) + 1);
        }
    }

    public long BreakRevision(long ship)
    {
        lock (_gate)
        {
            return _breaks.GetValueOrDefault(ship);
        }
    }

    public void Add(long ship, long target, bool silent = false)
    {
        lock (_gate)
        {
            if (_locks.Add((ship, target)))
            {
                _outgoing[ship] = _outgoing.GetValueOrDefault(ship) + 1;
            }
            if (_pending.Remove((ship, target)))
            {
                RemovedOutgoing(ship);
            }
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

    public bool ContainsPending(long ship, long target)
    {
        lock (_gate)
        {
            return _pending.Contains((ship, target));
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
            if (_locks.Remove((ship, target)))
            {
                RemovedOutgoing(ship);
            }
            if (_pending.Remove((ship, target)))
            {
                RemovedOutgoing(ship);
            }
            _silent.Remove((ship, target));
        }
    }

    public void RemoveShip(long ship)
    {
        lock (_gate)
        {
            RemoveEdges(_locks, ship);
            RemoveEdges(_pending, ship);
            _silent.RemoveWhere(edge => edge.Ship == ship || edge.Target == ship);
        }
    }

    public bool IsTargeted(long target)
    {
        lock (_gate)
        {
            return _locks.Any(edge => edge.Target == target);
        }
    }

    public bool HasOutgoing(long ship)
    {
        lock (_gate)
        {
            return _outgoing.ContainsKey(ship);
        }
    }

    public void Pending(long ship, long target)
    {
        lock (_gate)
        {
            if (_pending.Add((ship, target)))
            {
                _outgoing[ship] = _outgoing.GetValueOrDefault(ship) + 1;
            }
        }
    }

    private void RemovedOutgoing(long ship)
    {
        int count = _outgoing[ship] - 1;
        if (count == 0)
        {
            _outgoing.Remove(ship);
        }
        else
        {
            _outgoing[ship] = count;
        }
    }

    private void RemoveEdges(HashSet<(long Ship, long Target)> edges, long ship) =>
        edges.RemoveWhere(edge =>
        {
            if (edge.Ship != ship && edge.Target != ship)
            {
                return false;
            }
            RemovedOutgoing(edge.Ship);
            return true;
        });

    public long[] Targeters(long target)
    {
        lock (_gate)
        {
            return _locks.Where(edge => edge.Target == target && !_silent.Contains(edge)).Select(edge => edge.Ship).Order().ToArray();
        }
    }
}
