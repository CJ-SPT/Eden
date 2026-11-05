using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Eden.Protocol;
using Eden.Protocol.PilotSessions;
using Eden.Server.Chat;
using Eden.Server.Launcher;

namespace Eden.Server.Authentication;

[Injectable(InjectionType.Singleton)]
internal sealed class DevelopmentIdentity(TimeProvider clock, LauncherStore accounts, LauncherOptions options) : ILoginIdentity, IDisposable
{
    private const string KeyName = "Eden.LocalLogin.3662168";

    private RSA? _key = OperatingSystem.IsWindows() ? Open() : null;
    public RSA Key => _key ?? throw new InvalidDataException("The local pilot session is unavailable.");

    public void Dispose() => Interlocked.Exchange(ref _key, null)?.Dispose();

    [SupportedOSPlatform("windows")]
    internal static RSACng Open()
    {
        using CngKey key = CngKey.Exists(KeyName)
            ? CngKey.Open(KeyName)
            : CngKey.Create(
                CngAlgorithm.Rsa,
                KeyName,
                new CngKeyCreationParameters
                {
                    Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) },
                }
            );
        return new RSACng(key);
    }

    internal string Prepare(string root, long accountId, ChatCertificate chatCertificate)
    {
        if (!File.Exists(Path.Combine(root, ".eden-client-ready")))
        {
            throw new InvalidDataException("Prepare the isolated development copy first.");
        }

        string path = Path.Combine(root, "tq", "manifest.dat");
        byte[] original = File.ReadAllBytes(path);
        byte[] prefix = ClientManifest.ReadOriginalPrefix(original);
        string chatHash = chatCertificate.TrustClient(root);
        byte[] local = SignManifest(Convert.ToBase64String(prefix), chatHash);
        if (!original.AsSpan().SequenceEqual(local))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, local);
            File.Move(temporary, path, overwrite: true);
        }

        string claim =
            $"eden2.{accountId}.{clock.GetUtcNow().ToUnixTimeSeconds()}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        return claim
            + "."
            + Convert.ToHexString(Key.SignData(Encoding.ASCII.GetBytes(claim), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public long? ValidateTicket(string ticket)
    {
        if (ticket.StartsWith("eden3.", StringComparison.Ordinal))
        {
            return accounts.ConsumeTicket(ticket);
        }
        if (!options.DevelopmentLogin)
        {
            return null;
        }
        string[] parts = ticket.Split('.');
        // Version 1 tickets identify the original account until their normal expiry.
        long? account = parts switch
        {
            ["eden1", _, _, _] or ["eden2", "1000001", _, _, _] => 1000001,
            ["eden2", "1000002", _, _, _] => 1000002,
            _ => null,
        };
        if (
            account is null
            || !long.TryParse(parts[^3], out long timestamp)
            || timestamp < clock.GetUtcNow().ToUnixTimeSeconds() - 180
            || timestamp > clock.GetUtcNow().ToUnixTimeSeconds() + 5
            || parts[^2].Length != 32
            || parts[^1].Length != Key.KeySize / 4
            || !parts[^1].All(Uri.IsHexDigit)
        )
        {
            return null;
        }

        return Key.VerifyData(
            Encoding.ASCII.GetBytes(string.Join('.', parts[..^1])),
            Convert.FromHexString(parts[^1]),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1
        )
            ? account
            : null;
    }

    internal byte[] SignManifest(string encodedPrefix, string? chatBundleSha256 = null)
    {
        byte[] prefix;
        try
        {
            prefix = Convert.FromBase64String(encodedPrefix);
        }
        catch (FormatException)
        {
            throw new LauncherException("invalid_manifest", "Invalid manifest prefix.");
        }
        if (!ClientManifest.IsOriginal(prefix))
        {
            throw new LauncherException("invalid_manifest", "Only the original build 3662168 manifest is supported.");
        }
        prefix = ClientManifest.WithChatHash(prefix, chatBundleSha256);
        lock (Key)
        {
            using MemoryStream buffer = new();
            using BinaryWriter writer = new(buffer);
            writer.Write(prefix);
            byte[] pem = Encoding.ASCII.GetBytes(Key.ExportSubjectPublicKeyInfoPem() + "\n");
            writer.Write(pem.Length);
            writer.Write(pem);
            byte[] signature = Key.SignData(SHA256.HashData(buffer.ToArray()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            writer.Write(signature.Length);
            writer.Write(signature);
            return buffer.ToArray();
        }
    }

    public long ReadRoles(long accountId) => accounts.ReadRoles(accountId);
}
