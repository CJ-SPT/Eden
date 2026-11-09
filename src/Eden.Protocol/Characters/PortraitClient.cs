using System.Text;
using System.Text.Json;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Characters;

internal static class PortraitClient
{
    private static readonly Lazy<byte[]> s_expression = new(CreateExpression);

    public static byte[] Expression => s_expression.Value;

    private static byte[] CreateExpression()
    {
        using var stream = typeof(PortraitClient).Assembly.GetManifestResourceStream("Eden.Protocol.Characters.portrait_client.py")!;
        using StreamReader reader = new(stream, Encoding.UTF8);
        // GPS.__Execute verifies the host signature, then evals a Python 2 marshal
        // string. JSON escaping is also valid for this ASCII Python source literal.
        // Use an isolated namespace and keep the native challenge result empty.
        string source = JsonSerializer.Serialize(reader.ReadToEnd());
        byte[] expression = Encoding.ASCII.GetBytes($"eval(compile(u{source}, 'eden-portraits', 'exec'), {{}}) or {{}}");
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
