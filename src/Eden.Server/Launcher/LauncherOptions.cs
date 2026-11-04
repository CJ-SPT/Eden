using System.Net;
using System.Text.Json;

namespace Eden.Server.Launcher;

public sealed record LauncherOptions
{
    public string ServerId { get; init; } = "eden-local";
    public string Name { get; init; } = "Eden Local";
    public string ListenAddress { get; init; } = "127.0.0.1";
    public int GamePort { get; init; } = 26000;
    public int GatewayPort { get; init; } = 26001;
    public int ApiPort { get; init; } = 26002;
    public string GameAddress { get; init; } = "127.0.0.1:26000";
    public string GatewayAddress { get; init; } = "http://127.0.0.1:26001";
    public string? CertificatePath { get; init; }
    public bool DevelopmentLogin { get; init; }

    public static LauncherOptions Load(string? path)
    {
        var options = path is null ? new() : JsonSerializer.Deserialize<LauncherOptions>(File.ReadAllText(path))!;
        options.Validate();
        return options;
    }

    public void Validate()
    {
        var address = IPAddress.Parse(ListenAddress);
        bool local = IPAddress.IsLoopback(address);
        var gateway = new Uri(GatewayAddress);
        if (!local && (string.IsNullOrWhiteSpace(CertificatePath) || gateway.Scheme != "https" || DevelopmentLogin))
        {
            throw new InvalidDataException("Remote hosting requires a TLS certificate, HTTPS gateway, and development login disabled.");
        }
        if (DevelopmentLogin && !local)
        {
            throw new InvalidDataException("Development login requires a loopback listener.");
        }
        if (
            new[] { GamePort, GatewayPort, ApiPort }.Any(port => port is < 1 or > 65535)
            || new[] { GamePort, GatewayPort, ApiPort }.Distinct().Count() != 3
            || string.IsNullOrWhiteSpace(ServerId)
            || string.IsNullOrWhiteSpace(Name)
        )
        {
            throw new InvalidDataException("Specify a server identity and three distinct valid ports.");
        }
    }
}
