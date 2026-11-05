using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Eden.Protocol;
using Eden.Server.Persistence;

namespace Eden.Server.Chat;

[Injectable(InjectionType.Singleton)]
internal sealed class ChatCertificate : IDisposable
{
    // Schannel needs a user key container during TLS negotiation. UserKeySet
    // avoids the Windows ephemeral-key failure and is released with the cert.
    private static X509KeyStorageFlags StorageFlags =>
        OperatingSystem.IsWindows() ? X509KeyStorageFlags.UserKeySet : X509KeyStorageFlags.EphemeralKeySet;

    public X509Certificate2 Certificate { get; }
    public string PublicPem => Certificate.ExportCertificatePem();

    public ChatCertificate(WorldDatabase database, TimeProvider clock)
    {
        string path = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(database.Path))!, "chat-tls.pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
        {
            Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, StorageFlags);
            return;
        }
        using RSA key = RSA.Create(3072);
        CertificateRequest request = new("CN=Eden Chat", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign,
                true
            )
        );
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        SubjectAlternativeNameBuilder names = new();
        names.AddDnsName(ChatServer.Domain);
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(clock.GetUtcNow().AddDays(-1), clock.GetUtcNow().AddYears(5));
        byte[] bytes = generated.Export(X509ContentType.Pfx);
        string temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporary, bytes);
            try
            {
                File.Move(temporary, path);
            }
            catch (IOException) when (File.Exists(path)) { }
        }
        finally
        {
            File.Delete(temporary);
            CryptographicOperations.ZeroMemory(bytes);
        }
        Certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null, StorageFlags);
    }

    internal string TrustClient(string root)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!File.Exists(Path.Combine(root, ".eden-client-ready")))
        {
            throw new InvalidDataException("Chat trust requires a prepared Eden client copy.");
        }
        string bundle = Path.Combine(root, "tq", "bin64", "packages", "certifi", "cacert.pem");
        for (string? current = bundle; current is not null && current.Length >= root.Length; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Chat trust cannot modify linked client files.");
            }
        }
        string existing = File.ReadAllText(bundle);
        if (!existing.Contains(PublicPem, StringComparison.Ordinal))
        {
            string temporary = bundle + "." + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, existing + "\n" + PublicPem + "\n");
                File.Move(temporary, bundle, overwrite: true);
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(bundle)));
    }

    public void Dispose() => Certificate.Dispose();
}
