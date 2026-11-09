using System.Reflection;
using System.Text;

namespace Eden.Server;

internal static class ServerBanner
{
    private const string LeftMargin = "    ";
    private const string InnerPadding = "  ";

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
        string[] lines =
        [
            "",
            @" _____  ____   _____  _   _",
            @"| ____||  _ \ | ____|| \ | |",
            @"|  _|  | | | ||  _|  |  \| |",
            @"| |___ | |_| || |___ | |\  |",
            @"|_____||____/ |_____||_| \_|",
            "",
            $"Version:  {version}",
            $"Revision: {revision}",
            "",
        ];
        int width = lines.Max(line => line.Length);
        string border = $"{LeftMargin}+{new string('-', width + InnerPadding.Length * 2)}+";
        StringBuilder banner = new();
        banner.AppendLine();
        banner.AppendLine(border);
        foreach (string line in lines)
        {
            banner.Append(LeftMargin).Append('|').Append(InnerPadding).Append(line.PadRight(width)).Append(InnerPadding).AppendLine("|");
        }
        return banner.AppendLine(border).ToString();
    }
}
