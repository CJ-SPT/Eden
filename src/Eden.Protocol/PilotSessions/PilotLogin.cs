using System.Security.Cryptography;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
public sealed class PilotLogin(ILoginIdentity identity, IPilotSession session)
{
    private static long s_nextClientId = DateTime.UtcNow.Ticks;
    internal const long PlayerRole = 1L << 61;
    internal const int NodeId = 10001;

    public async Task RunAsync(
        Stream stream,
        byte[] firstCommand,
        Action<string> report,
        CancellationToken cancellationToken,
        Action<long>? authenticated = null
    )
    {
        object?[] vk = Tuple(BlueMarshal.Decode(firstCommand), 3);
        if (vk[0] is not null || BlueMarshal.Text(vk[1]) != "VK")
        {
            throw new InvalidDataException("Expected a local login request.");
        }

        object?[] exchange = Tuple(BlueMarshal.Decode(await PacketStream.ReadAsync(stream, cancellationToken)), 2);
        var keys = Fields(exchange[1]);
        RSA key = identity.Key;
        using Aes aes = Aes.Create();
        aes.Key = key.Decrypt(Bytes(keys["crypting_sessionkey"]), RSAEncryptionPadding.Pkcs1);
        aes.IV = key.Decrypt(Bytes(keys["crypting_sessioniv"]), RSAEncryptionPadding.Pkcs1);
        await PacketStream.WriteAsync(stream, BlueMarshal.Encode("OK CC"), cancellationToken);
        report("Negotiated encrypted login transport.");

        EncryptedPackets packets = new(stream, aes, cancellationToken);
        object?[] credentials = Tuple(await packets.ReadAsync(), 2);
        var fields = Fields(credentials[1]);
        if (identity.ValidateTicket(BlueMarshal.Text(fields["user_sso_token"])) is not long accountId)
        {
            throw new InvalidDataException("Invalid or expired local login ticket.");
        }

        await packets.WriteAsync(2);
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
        await packets.WriteAsync(
            new object?[] { challenge, new object?[] { expression, signature }, new Dictionary<string, object?>(), response }
        );
        object?[] proof = Tuple(await packets.ReadAsync(), 3);
        if (!CryptographicOperations.FixedTimeEquals(Bytes(proof[0]), HashArgument(challenge)))
        {
            throw new InvalidDataException("Client challenge response mismatch.");
        }

        authenticated?.Invoke(accountId);
        long clientId = Interlocked.Increment(ref s_nextClientId);
        await packets.WriteAsync(
            new Dictionary<string, object?>
            {
                ["session_init"] = AccountState(accountId),
                ["session_id"] = clientId,
                ["sessionID"] = clientId,
                ["clientID"] = clientId,
                ["access_token"] = "",
                ["computer_hash"] = "",
                ["client_hash"] = "Unknown",
            }
        );
        report("Authenticated local pilot account; session established.");

        // Build 3662168 expects a signed Python 2.7 function body for its optional settings statistics.
        // marshal.dumps((lambda: {}).__code__) returns no settings and captures no client data.
        byte[] settingsCode = BlueMarshal.Encode(
            Convert.FromHexString(
                "630000000000000000010000004300000073040000006900005328010000004E280000000028000000002800000000280000"
                    + "000074080000003C737472696E673E74080000003C6C616D6264613E010000007300000000"
            )
        );
        byte[] settingsSignature = key.SignData(HashArgument(settingsCode), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        await session.RunAsync(
            packets.ReadAsync,
            packets.WriteAsync,
            report,
            accountId,
            clientId,
            [settingsCode, settingsSignature],
            cancellationToken
        );
    }

    internal static Dictionary<string, object?> AccountState(long accountId) =>
        new()
        {
            ["userid"] = accountId,
            ["role"] = PlayerRole,
            ["languageID"] = "EN",
            ["userType"] = 1,
        };

    internal static byte[] Bytes(object? value) => value as byte[] ?? throw new InvalidDataException("Expected bytes.");

    internal static Dictionary<string, object?> Fields(object? value) =>
        value as Dictionary<string, object?> ?? throw new InvalidDataException("Expected fields.");

    internal static object?[] Tuple(object? value, int length) =>
        value is object?[] tuple && tuple.Length == length
            ? tuple
            : throw new InvalidDataException($"Expected a tuple with {length} fields.");

    private static byte[] HashArgument(byte[] value) => SHA256.HashData(BlueMarshal.Encode(new object?[] { value }));
}
