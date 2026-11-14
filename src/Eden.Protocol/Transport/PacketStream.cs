using System.Buffers.Binary;

namespace Eden.Protocol.Transport;

public static class PacketStream
{
    // The high four length bits are Carbon transport flags. Startup uses plain frames.
    private const int MaxHandshakeBytes = 16 * 1024;

    // Local session bound, separate from the small startup exchange. Full ballparks
    // exceed 16 KiB; the high four header bits remain unsupported transport flags.
    internal const int MaxSessionBytes = 1024 * 1024;

    // Callers reserve the first four bytes and retain the frame until this write completes.
    internal static async Task WriteFrameAsync(
        Stream stream,
        Memory<byte> frame,
        CancellationToken cancellationToken,
        int maxPayloadBytes,
        TimeProvider? clock = null
    )
    {
        int length = frame.Length - sizeof(uint);
        if (length <= 0 || length > maxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(frame));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(frame.Span, (uint)length);
        using StreamDeadline deadline = new(cancellationToken, clock);
        await stream.WriteAsync(frame, deadline.Token);
    }

    public static async Task<byte[]> ReadAsync(
        Stream stream,
        CancellationToken cancellationToken,
        int maxPayloadBytes = MaxHandshakeBytes,
        TimeProvider? clock = null
    )
    {
        byte[] header = new byte[sizeof(uint)];
        // Idle sessions may remain quiet. Once a frame starts, trickled bytes cannot extend its deadline.
        await stream.ReadExactlyAsync(header.AsMemory(0, 1), cancellationToken);
        using StreamDeadline deadline = new(cancellationToken, clock);
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maxPayloadBytes)
        {
            throw new InvalidDataException($"Unsupported packet frame header 0x{length:X8}.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, deadline.Token);
        return payload;
    }

    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken,
        int maxPayloadBytes = MaxHandshakeBytes,
        TimeProvider? clock = null
    )
    {
        if (payload.Length == 0 || payload.Length > maxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        byte[] header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);
        using StreamDeadline deadline = new(cancellationToken, clock);
        await stream.WriteAsync(header, deadline.Token);
        await stream.WriteAsync(payload, deadline.Token);
    }
}
