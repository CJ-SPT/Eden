using System.IO.Compression;
using System.Security.Cryptography;

namespace Eden.Protocol;

public sealed class PilotLogin(RSA key, Func<string, bool> acceptTicket)
{
    internal const int UserId = 1000001;
    internal const int PilotId = 90000001;
    private static long s_nextClientId = DateTime.UtcNow.Ticks;
    internal const long PlayerRole = 1L << 61;
    internal const int NodeId = 10001;

    public async Task RunAsync(Stream stream, byte[] firstCommand, Action<string> report, CancellationToken cancellationToken, Action? authenticated = null)
    {
        object?[] vk = Tuple(BlueMarshal.Decode(firstCommand), 3);
        if (vk[0] is not null || BlueMarshal.Text(vk[1]) != "VK")
        {
            throw new InvalidDataException("Expected a local login request.");
        }

        object?[] exchange = Tuple(BlueMarshal.Decode(await PacketStream.ReadAsync(stream, cancellationToken)), 2);
        var keys = Fields(exchange[1]);
        using Aes aes = Aes.Create();
        aes.Key = key.Decrypt(Bytes(keys["crypting_sessionkey"]), RSAEncryptionPadding.Pkcs1);
        aes.IV = key.Decrypt(Bytes(keys["crypting_sessioniv"]), RSAEncryptionPadding.Pkcs1);
        await PacketStream.WriteAsync(stream, BlueMarshal.Encode("OK CC"), cancellationToken);
        report("Negotiated encrypted login transport.");

        async Task<object?> ReadAsync()
        {
            byte[] ciphertext = await PacketStream.ReadAsync(stream, cancellationToken);
            byte[] plain = aes.DecryptCbc(ciphertext, aes.IV);
            if (plain.Length > 0 && plain[0] == 0x78)
            {
                using ZLibStream inflater = new(new MemoryStream(plain), CompressionMode.Decompress);
                byte[] decoded = new byte[65537];
                int length = inflater.ReadAtLeast(decoded, decoded.Length, throwOnEndOfStream: false);
                if (length == decoded.Length) { throw new InvalidDataException("Oversized compressed packet."); }
                plain = decoded[..length];
            }
            return BlueMarshal.Decode(plain);
        }

        async Task WriteAsync(object? value)
        {
            byte[] ciphertext = aes.EncryptCbc(BlueMarshal.Encode(value), aes.IV);
            await PacketStream.WriteAsync(stream, ciphertext, cancellationToken);
        }

        object?[] credentials = Tuple(await ReadAsync(), 2);
        var fields = Fields(credentials[1]);
        if (!acceptTicket(BlueMarshal.Text(fields["user_sso_token"])))
        {
            throw new InvalidDataException("Invalid or expired local login ticket.");
        }

        await WriteAsync(2);
        byte[] challenge = RandomNumberGenerator.GetBytes(64);
        // Python 2 marshal string containing the fixed expression "{}". The client verifies
        // the local signature, evaluates it, and returns the empty dictionary as its result.
        byte[] expression = BlueMarshal.Encode(new byte[] { (byte)'s', 2, 0, 0, 0, (byte)'{', (byte)'}' });
        byte[] signature = key.SignData(HashArgument(expression), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        Dictionary<string, object?> response = new()
        {
            ["challenge_responsehash"] = HashArgument(Bytes(credentials[0])),
            ["macho_version"] = 496,
            ["boot_version"] = 24.01,
            ["boot_build"] = CompatibilityHandshake.Build,
            ["boot_codename"] = "V24.01",
            ["boot_region"] = "ccp",
            ["cluster_usercount"] = 1,
            ["proxy_nodeid"] = NodeId,
            ["user_logonqueueposition"] = 0,
            ["config_vals"] = new Dictionary<string, object?>(),
        };
        await WriteAsync(new object?[] { challenge, new object?[] { expression, signature }, new Dictionary<string, object?>(), response });
        object?[] proof = Tuple(await ReadAsync(), 3);
        if (!CryptographicOperations.FixedTimeEquals(Bytes(proof[0]), HashArgument(challenge)))
        {
            throw new InvalidDataException("Client challenge response mismatch.");
        }

        long clientId = Interlocked.Increment(ref s_nextClientId);
        await WriteAsync(new Dictionary<string, object?>
        {
            ["session_init"] = AccountState(),
            ["session_id"] = clientId,
            ["sessionID"] = clientId,
            ["clientID"] = clientId,
            ["access_token"] = "",
            ["computer_hash"] = "",
            ["client_hash"] = "Unknown",
        });
        authenticated?.Invoke();
        report("Authenticated local pilot account; session established.");

        await PilotServices.RunAsync(ReadAsync, WriteAsync, report, clientId);
    }

    internal static Dictionary<string, object?> AccountState() => new()
    {
        ["userid"] = UserId,
        ["role"] = PlayerRole,
        ["languageID"] = "EN",
        ["userType"] = 1,
    };

    internal static byte[] Bytes(object? value) => value as byte[] ?? throw new InvalidDataException("Expected bytes.");
    internal static Dictionary<string, object?> Fields(object? value) => value as Dictionary<string, object?> ?? throw new InvalidDataException("Expected fields.");
    internal static object?[] Tuple(object? value, int length) => value is object?[] tuple && tuple.Length == length ? tuple : throw new InvalidDataException($"Expected a tuple with {length} fields.");
    private static byte[] HashArgument(byte[] value) => SHA256.HashData(BlueMarshal.Encode(new object?[] { value }));
}
