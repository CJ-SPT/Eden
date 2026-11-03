using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

public sealed record SignedSettings(byte[] Code, byte[] Signature) : IBlueWritable
{
    public static SignedSettings Empty { get; } = new([], []);

    public object ToBlue() => new object?[] { Code, Signature };
}
