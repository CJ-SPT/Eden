using System.Security.Cryptography;
using Eden.Server.Launcher;

namespace Eden.Server.Authentication;

internal static class ClientManifest
{
    internal const int PrefixLength = 17769;
    internal const string PrefixHash = "04EB4A00671E7289BF14AAFBEE6849D490678A291DAD0DF333E519913921C631";

    // Build 3662168's version-4 manifest: only packages/certifi/cacert.pem varies.
    // The separate bin64/cacert.pem entry and all other hashes stay pinned.
    internal const int ChatHashOffset = 1479;
    internal const string OriginalChatHash = "54ABC073292AB1B3EA95361D7E9FBC0FDF3C2B74777F0A4E2B6EC26670F0A453";
    internal const int CodeHashOffset = 34;
    internal const string OriginalCodeHash = "37FB2AF4FA0EC5C8FA320F34CD28C8B7DBF4917418336B38CBA20A7860DB0368";

    internal static byte[] WithCodeHash(byte[] prefix, string? hash)
    {
        byte[] result = prefix.ToArray();
        if (hash is not null)
        {
            if (hash.Length != SHA256.HashSizeInBytes * 2 || !hash.All(char.IsAsciiHexDigit))
            {
                throw new InvalidDataException("Invalid skill archive hash.");
            }
            Convert.FromHexString(hash).CopyTo(result, CodeHashOffset);
        }
        return result;
    }

    internal static byte[] ReadOriginalPrefix(byte[] manifest)
    {
        if (manifest.Length < PrefixLength)
        {
            throw new InvalidDataException("Unrecognized manifest prefix.");
        }
        byte[] prefix = manifest[..PrefixLength];
        Convert.FromHexString(OriginalChatHash).CopyTo(prefix, ChatHashOffset);
        if (!IsOriginal(prefix))
        {
            throw new InvalidDataException("Unrecognized manifest prefix.");
        }
        return prefix;
    }

    internal static bool IsOriginal(byte[] prefix) =>
        prefix.Length == PrefixLength && Convert.ToHexString(SHA256.HashData(prefix)) == PrefixHash;

    // The caller validates the complete canonical prefix before this wire edit.
    internal static byte[] WithChatHash(byte[] prefix, string? hash)
    {
        byte[] result = prefix.ToArray();
        if (hash is null)
        {
            return result;
        }
        if (hash.Length != SHA256.HashSizeInBytes * 2 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new LauncherException("invalid_manifest", "Invalid chat certificate bundle hash.");
        }
        Convert.FromHexString(hash).CopyTo(result, ChatHashOffset);
        return result;
    }
}
