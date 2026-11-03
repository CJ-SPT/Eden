using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal abstract record PilotCallResult
{
    private PilotCallResult() { }

    internal sealed record Success(IBlueWritable Reply) : PilotCallResult;

    internal sealed record UnsupportedCall : PilotCallResult;

    internal sealed record Failure(string Message) : PilotCallResult;

    internal sealed record NamedFailure(string Name, IBlueWritable Values) : PilotCallResult;

    public static PilotCallResult None { get; } = new Success(new NoneReply());

    private sealed record NoneReply : IBlueWritable
    {
        public object? ToBlue() => null;
    }

    // Removed once all service adapters return named replies directly.
    internal static PilotCallResult FromLegacy(object? value) =>
        value switch
        {
            null => None,
            PilotCallResult result => result,
            PilotCallRouter.Error error => new Failure(error.Message),
            PilotCallRouter.UserError error => new NamedFailure(error.Name, new LegacyReply(error.Values)),
            IBlueWritable reply => new Success(reply),
            _ => new Success(new LegacyReply(value)),
        };

    internal sealed record LegacyReply(object? Value) : IBlueWritable
    {
        public object? ToBlue() => Value;
    }
}

internal sealed record BoundPilotReply(BlueObjectReference Reference, IBlueWritable Reply) : IBlueWritable
{
    public object ToBlue() => new object?[] { Reference, Reply };
}
