using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

public sealed record SignedSettings(byte[] Code, byte[] Signature) : BlueModel
{
    public static SignedSettings Empty { get; } = new([], []);

    public override object ToBlue() => new object?[] { Code, Signature };
}
