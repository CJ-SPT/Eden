using Eden.Protocol.PilotSessions;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Development;

internal sealed record DeveloperCommandList(IReadOnlyList<string> Commands) : BlueModel
{
    internal static DeveloperCommandList For(long roles)
    {
        List<string> commands = [];
        if ((roles & (long)PilotRole.WorldMod) != 0)
        {
            commands.AddRange(["create", "createitem"]);
        }
        if ((roles & (long)PilotRole.GameMasterHigh) != 0)
        {
            commands.Add("giveskill");
        }
        if ((roles & (long)(PilotRole.GameMasterLow | PilotRole.GameMasterHigh)) != 0)
        {
            commands.Add("load");
        }
        if ((roles & (long)(PilotRole.GameMasterLow | PilotRole.WorldMod)) != 0)
        {
            commands.Add("tr");
        }
        return new(commands);
    }

    public override object ToBlue() =>
        // svc_slash.FetchRemoteCommandList deliberately calls SlashCmd('/'),
        // catches UserError, splits reason at ': ' and parses a Python list.
        // Only fixed server-owned command names belong in this literal.
        new Dictionary<string, object?>
        {
            ["reason"] = "Available commands: [" + string.Join(", ", Commands.Select(c => $"'{c}'")) + "]",
        };
}
