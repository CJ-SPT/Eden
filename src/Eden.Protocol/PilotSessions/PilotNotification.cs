namespace Eden.Protocol.PilotSessions;

internal abstract record PilotNotification(string Name)
{
    public abstract object?[] Arguments { get; }

    public void Deconstruct(out string name, out object?[] arguments)
    {
        name = Name;
        arguments = Arguments;
    }

    // Temporary bridge while each feature acquires named notification payloads.
    public static implicit operator PilotNotification((string Name, object?[] Arguments) notification) =>
        new LegacyNotification(notification.Name, notification.Arguments);

    private sealed record LegacyNotification(string EventName, object?[] Payload) : PilotNotification(EventName)
    {
        public override object?[] Arguments => Payload;
    }
}
