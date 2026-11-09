namespace Eden.Gameplay.Targeting;

public interface IPilotTargetLocks
{
    void Add(long ship, long target, bool silent = false);
    bool Contains(long ship, long target);
    void Remove(long ship, long target);
    void RemoveShip(long ship);
    long[] Targeters(long target);
}
