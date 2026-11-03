namespace Eden.Protocol.PilotSessions;

internal abstract record PilotNotification(string Name)
{
    // Only the packet adapter consumes this positional wire representation.
    public abstract object?[] Arguments { get; }
}
