using System.Security.Cryptography;
using System.Text;
using Eden.Protocol.Tests.Administration;
using Eden.Server.Authentication;
using Eden.Server.Launcher;
using Eden.World.Storage;

namespace Eden.Protocol.Tests.Launcher;

public sealed class ClientManifestTests
{
    [PinnedSkillClientFact]
    public void LauncherSignsPinnedManifestWithItsEmbeddedVerificationKey()
    {
        string client = Environment.GetEnvironmentVariable("EDEN_TEST_CLIENT_ROOT")!;
        byte[] source = File.ReadAllBytes(Path.Combine(client, "tq", "manifest.dat"));
        Convert.FromHexString(ClientManifest.OriginalCodeHash).CopyTo(source, ClientManifest.CodeHashOffset);
        byte[] prefix = ClientManifest.ReadOriginalPrefix(source);
        var store = new LauncherStore(new WorldDatabase("unused.sqlite"), TimeProvider.System);
        using var identity = new DevelopmentIdentity(TimeProvider.System, store, new LauncherOptions());

        byte[] manifest = identity.SignManifest(Convert.ToBase64String(prefix));

        using var reader = new BinaryReader(new MemoryStream(manifest));
        Assert.Equal(prefix, reader.ReadBytes(prefix.Length));
        string pem = Encoding.ASCII.GetString(reader.ReadBytes(reader.ReadInt32()));
        int signedLength = (int)reader.BaseStream.Position;
        byte[] signature = reader.ReadBytes(reader.ReadInt32());
        Assert.Equal(manifest.Length, reader.BaseStream.Position);
        using RSA verifier = RSA.Create();
        verifier.ImportFromPem(pem);
        Assert.True(
            verifier.VerifyData(
                SHA256.HashData(manifest.AsSpan(0, signedLength)),
                signature,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            )
        );
    }

    [Fact]
    public void ChatTrustChangesExactlyOneHashWithoutTouchingOtherEntriesOrTheOriginalPrefix()
    {
        byte[] original = RandomNumberGenerator.GetBytes(ClientManifest.PrefixLength);
        byte[] saved = original.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData("new trust bundle"u8));
        byte[] changed = ClientManifest.WithChatHash(original, hash);
        Assert.Equal(saved, original);
        Assert.Equal(original[..ClientManifest.ChatHashOffset], changed[..ClientManifest.ChatHashOffset]);
        int after = ClientManifest.ChatHashOffset + SHA256.HashSizeInBytes;
        Assert.Equal(original[after..], changed[after..]);
        Assert.Equal(Convert.FromHexString(hash), changed[ClientManifest.ChatHashOffset..after]);
        Assert.False(ClientManifest.IsOriginal(changed));
        Assert.Throws<InvalidDataException>(() => ClientManifest.ReadOriginalPrefix(changed));
        Assert.Equal(original, ClientManifest.WithChatHash(original, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("BAD")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public void InvalidChatHashCannotBecomePartOfASignedManifest(string hash)
    {
        byte[] original = new byte[ClientManifest.PrefixLength];
        Assert.Throws<LauncherException>(() => ClientManifest.WithChatHash(original, hash));
        Assert.Equal(new byte[ClientManifest.PrefixLength], original);
    }
}
