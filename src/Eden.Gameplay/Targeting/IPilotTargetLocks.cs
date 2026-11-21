namespace Eden.Gameplay.Targeting;

public interface IPilotTargetLocks
{
    void Add(long ship, long target, bool silent = false);
    bool Contains(long ship, long target);
    bool ContainsPending(long ship, long target) => false;
    void Remove(long ship, long target);
    void RemoveShip(long ship);
    long[] Targeters(long target);
    bool IsTargeted(long target) => Targeters(target).Length != 0;
    bool HasOutgoing(long ship) => false;
    void Pending(long ship, long target) { }
}
