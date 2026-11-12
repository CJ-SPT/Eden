using System.Security.Cryptography;
using Eden.Infrastructure.Registration;
using Eden.Protocol.Serialization;
using Eden.Protocol.Transport;

namespace Eden.Protocol.PilotSessions;

[Injectable(InjectionType.Scoped)]
public sealed class PilotLogin(ILoginIdentity identity, IPilotSession session, SessionDiagnostics? diagnostics = null)
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
        PilotLoginWire.ValidateCommand(firstCommand);

        var exchange = LoginKeyExchange.Read(BlueMarshal.Decode(await PacketStream.ReadAsync(stream, cancellationToken)));
        RSA key = identity.Key;
        using Aes aes = Aes.Create();
        aes.Key = key.Decrypt(exchange.SessionKey, RSAEncryptionPadding.Pkcs1);
        aes.IV = key.Decrypt(exchange.SessionIv, RSAEncryptionPadding.Pkcs1);
        await PacketStream.WriteAsync(stream, BlueMarshal.Encode("OK CC"), cancellationToken);
        report("Negotiated encrypted login transport.");

        EncryptedPackets packets = new(stream, aes, cancellationToken);
        var credentials = LoginCredentials.Read(await packets.ReadAsync());
        if (await identity.ValidateTicketAsync(credentials.Ticket, cancellationToken) is not long accountId)
        {
            throw new InvalidDataException("Invalid or expired local login ticket.");
        }

        await packets.WriteAsync(2);
        byte[] challenge = RandomNumberGenerator.GetBytes(64);
        // Install native UI compatibility adapters before the roster opens.
        // The signed expression returns no settings or other client data.
        byte[] expression = LoginClient.Expression;
        byte[] signature = key.SignData(PilotLoginWire.HashArgument(expression), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        await packets.WriteAsync(
            new LoginChallenge(challenge, new SignedSettings(expression, signature), PilotLoginWire.HashArgument(credentials.Challenge))
        );
        var proof = LoginProof.Read(await packets.ReadAsync());
        if (!CryptographicOperations.FixedTimeEquals(proof.ResponseHash, PilotLoginWire.HashArgument(challenge)))
        {
            throw new InvalidDataException("Client challenge response mismatch.");
        }

        authenticated?.Invoke(accountId);
        long clientId = Interlocked.Increment(ref s_nextClientId);
        await packets.WriteAsync(new LoginSession(accountId, clientId, identity.ReadRoles(accountId)));
        (diagnostics?.Information ?? report)($"Authenticated account {accountId}; session {clientId} established.");

        // Build 3662168 expects a signed Python 2.7 function body for its optional settings statistics.
        // marshal.dumps((lambda: {}).__code__) returns no settings and captures no client data.
        byte[] settingsCode = BlueMarshal.Encode(
            Convert.FromHexString(
                "630000000000000000010000004300000073040000006900005328010000004E280000000028000000002800000000280000"
                    + "000074080000003C737472696E673E74080000003C6C616D6264613E010000007300000000"
            )
        );
        byte[] settingsSignature = key.SignData(
            PilotLoginWire.HashArgument(settingsCode),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        );
        await session.RunAsync(
            packets.ReadAsync,
            packets.WriteAsync,
            report,
            accountId,
            clientId,
            new SignedSettings(settingsCode, settingsSignature),
            cancellationToken
        );
    }
}
