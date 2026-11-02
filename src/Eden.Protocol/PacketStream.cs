using System.Buffers.Binary;

namespace Eden.Protocol;

public static class PacketStream
{
    // The high four length bits are Carbon transport flags. Startup uses plain frames.
    private const int MaxHandshakeBytes = 16 * 1024;

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] header = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxHandshakeBytes)
        {
            throw new InvalidDataException($"Unsupported startup frame header 0x{length:X8}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaxHandshakeBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }
}
