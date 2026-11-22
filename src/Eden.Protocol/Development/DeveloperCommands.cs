using Eden.Protocol.PilotSessions;

namespace Eden.Protocol.Development;

internal static class DeveloperCommands
{
    internal const PilotRole WorldTools = PilotRole.GameMasterLow | PilotRole.WorldMod;
    private const PilotRole CargoTools = PilotRole.GameMasterLow | PilotRole.GameMasterHigh;

    internal static IReadOnlyDictionary<string, PilotRole> Roles { get; } =
        new Dictionary<string, PilotRole>(StringComparer.OrdinalIgnoreCase)
        {
            ["/create"] = PilotRole.WorldMod,
            ["/createitem"] = PilotRole.WorldMod,
            ["/extraction"] = WorldTools,
            ["/getpos"] = WorldTools,
            ["/giveskill"] = PilotRole.GameMasterHigh,
            ["/load"] = CargoTools,
            ["/stop"] = WorldTools,
            ["/structure"] = PilotRole.GameMasterHigh,
            ["/tr"] = WorldTools,
            ["/warptopoint"] = WorldTools,
        };

    internal static string[] Read(string text)
    {
        List<string> tokens = [];
        int index = 0;
        while (index < text.Length)
        {
            if (char.IsWhiteSpace(text[index]))
            {
                index++;
                continue;
            }
            if (text[index] is '\'' or '"')
            {
                char delimiter = text[index++];
                int end = text.IndexOf(delimiter, index);
                if (end < 0 || (end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])))
                {
                    throw new InvalidDataException("Close each quoted command argument before adding another.");
                }
                tokens.Add(text[index..end]);
                index = end + 1;
                continue;
            }
            int start = index++;
            while (index < text.Length && !char.IsWhiteSpace(text[index]))
            {
                index++;
            }
            tokens.Add(text[start..index]);
        }
        return tokens.ToArray();
    }
}
