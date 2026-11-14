using System.Text;
using System.Text.Json;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.PilotSessions;

internal static class ClientExpression
{
    internal static byte[] Create(string name, params string[] resources) => CreateWithPrelude(name, "", resources);

    internal static byte[] CreateWithPrelude(string name, string prelude, params string[] resources)
    {
        var sources = resources.Select(resource =>
        {
            using var stream = typeof(ClientExpression).Assembly.GetManifestResourceStream(resource)!;
            using StreamReader reader = new(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });
        // GPS.__Execute verifies the host signature, then evals a Python 2 marshal
        // string. JSON escaping is valid for this ASCII Python source literal.
        // Keep the native challenge result empty and collect no client data.
        string source = JsonSerializer.Serialize(prelude + string.Join("\n", sources));
        byte[] expression = Encoding.ASCII.GetBytes($"eval(compile(u{source}, '{name}', 'exec'), {{}}) or {{}}");
        using MemoryStream buffer = new();
        using (BinaryWriter writer = new(buffer, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write((byte)'s');
            writer.Write(expression.Length);
            writer.Write(expression);
        }
        return BlueMarshal.Encode(buffer.ToArray());
    }
}
