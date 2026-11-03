using System.IO.Compression;
using System.Security.Cryptography;
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
        byte[] plain = BlueMarshal.Encode(value);
        if (plain.Length > MaximumOutgoingBytes)
        {
            throw new InvalidDataException("Oversized outgoing session message.");
        }
        if (plain.Length > CompressionThreshold)
        {
            // Build 3662168's transport inflates a zlib envelope after decryption,
            // the same form accepted by ReadAsync. Incoming bounds stay unchanged.
            using MemoryStream buffer = new();
            using (ZLibStream compressor = new(buffer, CompressionLevel.Fastest, leaveOpen: true))
            {
                compressor.Write(plain);
            }
            if (buffer.Length < plain.Length)
            {
                plain = buffer.ToArray();
            }
        }
        byte[] ciphertext = aes.EncryptCbc(plain, aes.IV);
        await PacketStream.WriteAsync(stream, ciphertext, cancellationToken, PacketStream.MaxSessionBytes);
    }
}
