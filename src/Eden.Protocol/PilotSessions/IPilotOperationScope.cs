namespace Eden.Protocol.PilotSessions;

public interface IPilotOperationScope
{
    IDisposable Begin();
}
