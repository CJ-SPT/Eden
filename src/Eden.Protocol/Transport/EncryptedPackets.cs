using System.IO.Compression;
using System.Security.Cryptography;
using Eden.Protocol.Serialization;

namespace Eden.Protocol.Transport;

internal sealed class EncryptedPackets(Stream stream, Aes aes, CancellationToken cancellationToken)
{
    public async Task<object?> ReadAsync()
    {
        byte[] ciphertext = await PacketStream.ReadAsync(stream, cancellationToken, PacketStream.MaxSessionBytes);
        byte[] plain = aes.DecryptCbc(ciphertext, aes.IV);
        if (plain.Length > 0 && plain[0] == 0x78)
        {
            using ZLibStream inflater = new(new MemoryStream(plain), CompressionMode.Decompress);
            byte[] decoded = new byte[PacketStream.MaxSessionBytes + 1];
            int length = inflater.ReadAtLeast(decoded, decoded.Length, throwOnEndOfStream: false);
            if (length == decoded.Length) { throw new InvalidDataException("Oversized compressed packet."); }
            plain = decoded[..length];
        }
        return BlueMarshal.Decode(plain);
    }

    public async Task WriteAsync(object? value)
    {
        byte[] ciphertext = aes.EncryptCbc(BlueMarshal.Encode(value), aes.IV);
        await PacketStream.WriteAsync(stream, ciphertext, cancellationToken, PacketStream.MaxSessionBytes);
    }
}
