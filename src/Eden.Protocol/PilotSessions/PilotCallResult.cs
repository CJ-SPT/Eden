using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal abstract record PilotCallResult
{
    private PilotCallResult() { }

    internal sealed record Success(BlueModel Reply) : PilotCallResult;

    internal sealed record UnsupportedCall : PilotCallResult;

    internal sealed record Failure(string Message, bool ShowInformationDialog = false) : PilotCallResult;

    internal sealed record NamedFailure(string Name, BlueModel Values) : PilotCallResult;

    internal sealed record ClientCall(PilotClientDialog Dialog) : PilotCallResult;

    public static PilotCallResult None { get; } = new Success(new NoneReply());
    public static PilotCallResult Unsupported { get; } = new UnsupportedCall();

    public static implicit operator PilotCallResult(BlueModel reply) => new Success(reply);

    public static implicit operator PilotCallResult(bool value) => new Success(new ScalarReply<bool>(value));

    public static implicit operator PilotCallResult(int value) => new Success(new ScalarReply<int>(value));

    public static implicit operator PilotCallResult(long value) => new Success(new ScalarReply<long>(value));

    public static implicit operator PilotCallResult(double value) => new Success(new ScalarReply<double>(value));

    public static implicit operator PilotCallResult(string value) => new Success(new ScalarReply<string>(value));

    public static implicit operator PilotCallResult(BlueObjectReference value) => new Success(new BoundReferenceReply(value));

    public static implicit operator PilotCallResult(BlueConstructor value) => new Success(new ConstructedBlueReply(value));

    private sealed record ScalarReply<T>(T Value) : BlueModel
    {
        public override object? ToBlue() => Value;
    }

    private sealed record NoneReply : BlueModel
    {
        public override object? ToBlue() => null;
    }

    private sealed record BoundReferenceReply(BlueObjectReference Reference) : BlueModel
    {
        public override object ToBlue() => Reference;
    }

    // Native row descriptors are codec values, constructed only by wire adapters.
    private sealed record ConstructedBlueReply(BlueConstructor Constructor) : BlueModel
    {
        public override object ToBlue() => Constructor;
    }
}

internal sealed record BoundPilotReply(BlueObjectReference Reference, BlueModel Reply) : BlueModel
{
    public override object ToBlue() => new object?[] { Reference, Reply };
}
