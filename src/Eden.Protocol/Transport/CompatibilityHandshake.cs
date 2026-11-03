using System.Text;

namespace Eden.Protocol.Transport;

public static class CompatibilityHandshake
{
    public const int Build = 3662168;
    private const int HandshakeMarker = 170472;
    private const short MachoVersion = 496;
    private const double BootVersion = 24.01;

    // Blue marshal version 0 signature and four-byte shared-object count.
    private static readonly byte[] s_preamble = [0x7e, 0, 0, 0, 0];
    private static readonly byte[] s_queueCheck = [.. s_preamble, 0x2c, 0x01, 0x13, 2, (byte)'Q', (byte)'C'];
    private static readonly byte[] s_emptyQueue = [.. s_preamble, 0x08];
    private static readonly byte[] s_clientVersion = MakeVersionTuple(isServer: false);
    private static readonly byte[] s_greeting = MakeVersionTuple(isServer: true);

    public static async Task ServeAsync(
        Stream stream,
        Action<string> report,
        CancellationToken cancellationToken,
        Func<byte[], CancellationToken, Task>? login = null
    )
    {
        await PacketStream.WriteAsync(stream, s_greeting, cancellationToken);
        report("Sent server greeting.");
        byte[] version = await PacketStream.ReadAsync(stream, cancellationToken);
        if (!version.AsSpan().SequenceEqual(s_clientVersion))
        {
            throw new InvalidDataException($"Unexpected client compatibility reply ({version.Length} bytes).");
        }

        report($"Client confirmed build {Build} and MachoNet {MachoVersion}.");
        byte[] command = await PacketStream.ReadAsync(stream, cancellationToken);
        if (!command.AsSpan().SequenceEqual(s_queueCheck))
        {
            if (login is not null)
            {
                await login(command, cancellationToken);
                return;
            }

            throw new InvalidDataException($"Unsupported post-version request ({command.Length} bytes).");
        }

        await PacketStream.WriteAsync(stream, s_emptyQueue, cancellationToken);
        report("Answered queue check with position zero.");
    }

    private static byte[] MakeVersionTuple(bool isServer)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(s_preamble);
        writer.Write((byte)0x14); // Tuple with an explicit count.
        writer.Write((byte)(isServer ? 7 : 6));
        writer.Write((byte)0x04);
        writer.Write(HandshakeMarker);
        writer.Write((byte)0x05);
        writer.Write(MachoVersion);
        writer.Write((byte)0x08); // Zero logged-in pilots.
        writer.Write((byte)0x0a);
        writer.Write(BootVersion);
        writer.Write((byte)0x04);
        writer.Write(Build);
        WriteBytes(writer, "V24.01@ccp"u8);
        if (isServer)
        {
            WriteBytes(writer, "Unknown"u8); // No code update advertised.
        }

        return stream.ToArray();
    }

    private static void WriteBytes(BinaryWriter writer, ReadOnlySpan<byte> value)
    {
        writer.Write((byte)0x13);
        writer.Write(checked((byte)value.Length));
        writer.Write(value);
    }
}
