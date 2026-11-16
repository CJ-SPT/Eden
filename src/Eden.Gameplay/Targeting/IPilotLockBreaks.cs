namespace Eden.Gameplay.Targeting;

public interface IPilotLockBreaks
{
    void BreakOutgoing(long ship);
    long BreakRevision(long ship);
}
