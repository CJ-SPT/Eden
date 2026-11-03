namespace Eden.Protocol.PilotSessions;

public interface IPilotSession
{
    Task RunAsync(
        Func<Task<object?>> read,
        Func<object?, Task> write,
        Action<string> report,
        long accountId,
        long clientId,
        SignedSettings signedSettings,
        CancellationToken cancellationToken
    );
}
