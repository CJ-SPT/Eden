namespace Eden.Protocol.PilotSessions;

internal static class LoginClient
{
    private static readonly Lazy<byte[]> s_expression = new(() =>
        ClientExpression.Create(
            "eden-login",
            "Eden.Protocol.Characters.portrait_client.py",
            "Eden.Protocol.Flight.concord_overview_client.py",
            "Eden.Protocol.Docking.presence_lists_client.py"
        )
    );

    internal static byte[] Expression => s_expression.Value;
}
