using System.Reflection;

namespace Eden.Server;

internal static class ServerBanner
{
    internal static void Write(TextWriter output)
    {
        var assembly = typeof(ServerBanner).Assembly;
        string? version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        output.WriteLine(Create(version ?? assembly.GetName().Version?.ToString()));
    }

    internal static string Create(string? informationalVersion)
    {
        // The SDK appends the build's source revision after the informational version's '+'.
        string[] parts = informationalVersion?.Split('+', 2) ?? [];
        string version = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "unknown";
        string revision = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "unknown";
        return $"""

             _____  ____   _____  _   _
            | ____||  _ \ | ____|| \ | |
            |  _|  | | | ||  _|  |  \| |
            | |___ | |_| || |___ | |\  |
            |_____||____/ |_____||_| \_|

            EDEN Server
            Version:  {version}
            Revision: {revision}

            """;
    }
}
