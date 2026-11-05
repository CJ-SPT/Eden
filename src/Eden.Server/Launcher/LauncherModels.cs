namespace Eden.Server.Launcher;

public sealed record AccountRequest(string UserName, string Password, bool Developer = false, string? DeveloperCode = null);

public sealed record SignInRequest(string UserName, string Password, bool RememberMe);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LaunchRequest(int Build, string ManifestPrefix, string? ChatBundleSha256 = null);

public sealed record AccountReply(long AccountId, string UserName, bool Developer = false);

public sealed record SessionReply(
    long AccountId,
    string UserName,
    string AccessToken,
    long ExpiresAt,
    string? RefreshToken,
    bool Developer = false
);

public sealed record LaunchReply(string Ticket, long ExpiresAt, string Manifest);

public sealed record ApiError(string Code, string Message);

public sealed record ServerReply(
    string ServerId,
    string Name,
    int ApiVersion,
    int ClientBuild,
    string GameAddress,
    string GatewayAddress,
    string? ChatCertificatePem = null
);

internal sealed class LauncherException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}
