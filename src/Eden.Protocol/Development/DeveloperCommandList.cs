using Eden.Protocol.Serialization;

namespace Eden.Protocol.Development;

internal sealed record DeveloperCommandList(IReadOnlyList<string> Commands) : BlueModel
{
    internal static DeveloperCommandList For(long roles) =>
        new(
            DeveloperCommands
                .Roles.Where(command => (roles & (long)command.Value) != 0)
                .Select(command => command.Key[1..])
                .Order(StringComparer.Ordinal)
                .ToArray()
        );

    public override object ToBlue() =>
        // svc_slash.FetchRemoteCommandList deliberately calls SlashCmd('/'),
        // catches UserError, splits reason at ': ' and parses a Python list.
        // Only fixed server-owned command names belong in this literal.
        new Dictionary<string, object?>
        {
            ["reason"] = "Available commands: [" + string.Join(", ", Commands.Select(c => $"'{c}'")) + "]",
        };
}
