using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Characters;

internal static class PortraitClient
{
    private static readonly Lazy<byte[]> s_expression = new(() =>
        ClientExpression.Create("eden-portraits", "Eden.Protocol.Characters.portrait_client.py")
    );

    public static byte[] Expression => s_expression.Value;
}
