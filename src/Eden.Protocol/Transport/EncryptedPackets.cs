using System.IO.Compression;
using System.Security.Cryptography;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Eden.Infrastructure.Diagnostics;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Transport;

internal sealed class EncryptedPackets(Stream stream, Aes aes, CancellationToken cancellationToken)
{
    internal const int CompressionThreshold = 64 * 1024;
    internal const int CompactCompressionThreshold = 1024 * 1024;
    internal const int MaximumOutgoingBytes = 16 * 1024 * 1024;

    // Build 3662168 SocketGPS sets a 10 MiB packet limit. Reserve the four-byte frame header.
    internal const int MaximumOutgoingCiphertextBytes = 10 * 1024 * 1024 - sizeof(uint);

    public async Task<object?> ReadAsync()
    {
        byte[] ciphertext = await PacketStream.ReadAsync(stream, cancellationToken, PacketStream.MaxSessionBytes);
        byte[] plain = aes.DecryptCbc(ciphertext, aes.IV);
        if (plain.Length > 0 && plain[0] == 0x78)
        {
            using ZLibStream inflater = new(new MemoryStream(plain), CompressionMode.Decompress);
            byte[] decoded = new byte[PacketStream.MaxSessionBytes + 1];
            int length = inflater.ReadAtLeast(decoded, decoded.Length, throwOnEndOfStream: false);
            if (length == decoded.Length)
            {
                throw new InvalidDataException("Oversized compressed packet.");
            }
            plain = decoded[..length];
        }
        return BlueMarshal.Decode(plain);
    }

    public async Task WriteAsync(object? value)
    {
        using ArrayPoolBufferWriter<byte> plain = new();
        using (SessionLatency.Measure(SessionLatencyPhase.OutputEncode))
        {
            BlueMarshal.Encode(plain, value);
        }
        if (plain.WrittenCount > MaximumOutgoingBytes)
        {
            throw new OutgoingPacketSizeException("Encoded session message", plain.WrittenCount, MaximumOutgoingBytes);
        }
        if (plain.WrittenCount > CompressionThreshold)
        {
            // Build 3662168's transport inflates a zlib envelope after decryption,
            // the same form accepted by ReadAsync. Incoming bounds stay unchanged.
            using ArrayPoolBufferWriter<byte> buffer = new();
            // Large catalog replies spend more bandwidth than small updates. A bounded higher
            // compression level reduces their frame size without changing the zlib envelope.
            var level = plain.WrittenCount > CompactCompressionThreshold ? CompressionLevel.Optimal : CompressionLevel.Fastest;
            using (SessionLatency.Measure(SessionLatencyPhase.OutputCompress))
            using (ZLibStream compressor = new(buffer.AsStream(), level))
            {
                compressor.Write(plain.WrittenSpan);
            }
            await WriteEncryptedAsync(buffer.WrittenCount < plain.WrittenCount ? buffer.WrittenMemory : plain.WrittenMemory);
            return;
        }
        await WriteEncryptedAsync(plain.WrittenMemory);
    }

    private async Task WriteEncryptedAsync(ReadOnlyMemory<byte> plain)
    {
        int length = aes.GetCiphertextLengthCbc(plain.Length);
        if (length > MaximumOutgoingCiphertextBytes)
        {
            throw new OutgoingPacketSizeException("Encrypted session frame", length, MaximumOutgoingCiphertextBytes);
        }
        // Encrypt directly behind the frame header, then submit the complete frame in one write.
        // Keep this pooled memory until the asynchronous stream write has finished.
        using var frame = MemoryOwner<byte>.Allocate(sizeof(uint) + length);
        int written;
        using (SessionLatency.Measure(SessionLatencyPhase.OutputEncrypt))
        {
            written = aes.EncryptCbc(plain.Span, aes.IV, frame.Span[sizeof(uint)..]);
        }
        using (SessionLatency.Measure(SessionLatencyPhase.OutputWrite))
        {
            await PacketStream.WriteFrameAsync(
                stream,
                frame.Memory[..(sizeof(uint) + written)],
                cancellationToken,
                MaximumOutgoingCiphertextBytes
            );
        }
    }
}

// Raised only before the first byte is sent, so a call can safely send a smaller error instead.
internal sealed class OutgoingPacketSizeException(string stage, int size, int limit)
    : IOException($"{stage} is {size} bytes and exceeds the {limit}-byte limit.");
