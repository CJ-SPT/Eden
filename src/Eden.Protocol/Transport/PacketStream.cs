using System.Buffers.Binary;

namespace Eden.Protocol.Transport;

public static class PacketStream
{
    // The high four length bits are Carbon transport flags. Startup uses plain frames.
    private const int MaxHandshakeBytes = 16 * 1024;
    // Local session bound, separate from the small startup exchange. Full ballparks
    // exceed 16 KiB; the high four header bits remain unsupported transport flags.
    internal const int MaxSessionBytes = 1024 * 1024;

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken, int maxPayloadBytes = MaxHandshakeBytes)
    {
        byte[] header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maxPayloadBytes)
        {
            throw new InvalidDataException($"Unsupported packet frame header 0x{length:X8}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken, int maxPayloadBytes = MaxHandshakeBytes)
    {
        if (payload.Length == 0 || payload.Length > maxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }
}
