using System.Security.Cryptography;
using Eden.Server.Authentication;
using Eden.Server.Launcher;

namespace Eden.Protocol.Tests.Launcher;

public sealed class ClientManifestTests
{
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
