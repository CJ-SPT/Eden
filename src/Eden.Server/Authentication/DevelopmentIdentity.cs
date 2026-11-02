using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Eden.Protocol;
using Eden.Protocol.PilotSessions;

namespace Eden.Server.Authentication;

[Injectable(InjectionType.Singleton)]
internal sealed class DevelopmentIdentity(TimeProvider clock) : ILoginIdentity, IDisposable
{
    private const string KeyName = "Eden.LocalLogin.3662168";
    private const string ManifestHash = "895B2827174D7BDDC54870763E9D5AEA056066C01E3D56F0DDF76927DCD3AA26";

    private RSA? _key = OperatingSystem.IsWindows() ? Open() : null;
    public RSA Key => _key ?? throw new InvalidDataException("The local pilot session is unavailable.");
    public void Dispose() => Interlocked.Exchange(ref _key, null)?.Dispose();

    [SupportedOSPlatform("windows")]
    internal static RSACng Open()
    {
        using CngKey key = CngKey.Exists(KeyName) ? CngKey.Open(KeyName) : CngKey.Create(CngAlgorithm.Rsa, KeyName,
            new CngKeyCreationParameters { Parameters = { new CngProperty("Length", BitConverter.GetBytes(3072), CngPropertyOptions.None) } });
        return new RSACng(key);
    }

    internal string Prepare(string root)
    {
        if (!File.Exists(Path.Combine(root, ".eden-client-ready")))
        {
            throw new InvalidDataException("Prepare the isolated development copy first.");
        }

        string path = Path.Combine(root, "tq", "manifest.dat");
        byte[] original = File.ReadAllBytes(path);
        // Every byte through the public-key flag is part of the fingerprinted build.
        const int prefixLength = 17769;
        if (original.Length < prefixLength || Convert.ToHexString(SHA256.HashData(original.AsSpan(0, prefixLength))) !=
            "04EB4A00671E7289BF14AAFBEE6849D490678A291DAD0DF333E519913921C631")
        {
            throw new InvalidDataException("Unrecognized manifest prefix.");
        }

        using MemoryStream buffer = new();
        using BinaryWriter writer = new(buffer);
        writer.Write(original, 0, prefixLength);
        byte[] pem = Encoding.ASCII.GetBytes(Key.ExportSubjectPublicKeyInfoPem() + "\n");
        writer.Write(pem.Length);
        writer.Write(pem);
        byte[] signature = Key.SignData(SHA256.HashData(buffer.ToArray()), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        writer.Write(signature.Length);
        writer.Write(signature);
        byte[] local = buffer.ToArray();
        if (!original.AsSpan().SequenceEqual(local))
        {
            if (Convert.ToHexString(SHA256.HashData(original)) != ManifestHash)
            {
                throw new InvalidDataException("Restore the pinned manifest before provisioning this copy.");
            }

            string temporary = path + "." + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, local);
            File.Move(temporary, path, overwrite: true);
        }

        string claim = $"eden1.{clock.GetUtcNow().ToUnixTimeSeconds()}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16))}";
        return claim + "." + Convert.ToHexString(Key.SignData(Encoding.ASCII.GetBytes(claim), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    public bool ValidateTicket(string ticket)
    {
        string[] parts = ticket.Split('.');
        if (parts.Length != 4 || parts[0] != "eden1" || !long.TryParse(parts[1], out long timestamp) ||
            timestamp < clock.GetUtcNow().ToUnixTimeSeconds() - 180 || timestamp > clock.GetUtcNow().ToUnixTimeSeconds() + 5 ||
            parts[2].Length != 32 || parts[3].Length != Key.KeySize / 4 || !parts[3].All(Uri.IsHexDigit))
        {
            return false;
        }

        return Key.VerifyData(Encoding.ASCII.GetBytes(string.Join('.', parts[..3])), Convert.FromHexString(parts[3]),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
