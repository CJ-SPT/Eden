using System.IO.Compression;
using System.Security.Cryptography;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Transport;

internal sealed class EncryptedPackets(Stream stream, Aes aes, CancellationToken cancellationToken)
{
    internal const int CompressionThreshold = 64 * 1024;
    internal const int MaximumOutgoingBytes = 16 * 1024 * 1024;

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
        BlueMarshal.Encode(plain, value);
        if (plain.WrittenCount > MaximumOutgoingBytes)
        {
            throw new InvalidDataException("Oversized outgoing session message.");
        }
        if (plain.WrittenCount > CompressionThreshold)
        {
            // Build 3662168's transport inflates a zlib envelope after decryption,
            // the same form accepted by ReadAsync. Incoming bounds stay unchanged.
            using ArrayPoolBufferWriter<byte> buffer = new();
            using (ZLibStream compressor = new(buffer.AsStream(), CompressionLevel.Fastest))
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
        using var ciphertext = MemoryOwner<byte>.Allocate(aes.GetCiphertextLengthCbc(plain.Length));
        int written = aes.EncryptCbc(plain.Span, aes.IV, ciphertext.Span);
        await PacketStream.WriteAsync(stream, ciphertext.Memory[..written], cancellationToken, PacketStream.MaxSessionBytes);
    }
}
