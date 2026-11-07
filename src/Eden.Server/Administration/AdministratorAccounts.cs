using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eden.Data.World.Administration;
using Microsoft.AspNetCore.Identity;

namespace Eden.Server.Administration;

internal sealed class AdministratorAccounts(string worldPath)
{
    internal const string Scheme = "EdenAdministration";
    internal const string Policy = "LocalAdministrator";
    internal const string Role = "Administrator";
    private const string StampClaim = "eden:admin-stamp";
    private const string RealmClaim = "eden:admin-world";
    private readonly string _path = Path.GetFullPath(worldPath) + ".administrators.json";
    private readonly PasswordHasher<AdministratorCredential> _passwords = new();
    internal string Realm { get; } =
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(worldPath).ToUpperInvariant())))[..24];
    internal bool Provisioned => File.Exists(_path);

    internal void SetPassword(string name, string password)
    {
        name = name.Trim();
        if (
            name.Length is < 1 or > 64
            || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.')
            || password.Length is < 12 or > 128
        )
        {
            throw new InvalidDataException(
                "Use an administrator name of 1–64 letters/digits, dots, dashes or underscores " + "and a 12–128 character password."
            );
        }
        AdministratorCredential credential = new(name, "", Guid.NewGuid().ToString("N"));
        credential = credential with { PasswordHash = _passwords.HashPassword(credential, password) };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        string temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream file = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, credential, JsonSerializerOptions.Web);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    internal ClaimsPrincipal? Authenticate(string name, string password)
    {
        if (name.Length > 64 || password.Length > 128 || Read() is not { } credential)
        {
            return null;
        }
        bool verified = _passwords.VerifyHashedPassword(credential, credential.PasswordHash, password) != PasswordVerificationResult.Failed;
        if (!verified || !string.Equals(credential.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return new(
            new ClaimsIdentity(
                [
                    new(ClaimTypes.Name, credential.Name),
                    new(ClaimTypes.Role, Role),
                    new(StampClaim, credential.SecurityStamp),
                    new(RealmClaim, Realm),
                ],
                Scheme
            )
        );
    }

    internal bool IsCurrent(ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true
        && user.IsInRole(Role)
        && Read() is { } credential
        && user.Identity.Name == credential.Name
        && user.FindFirstValue(StampClaim) == credential.SecurityStamp
        && user.FindFirstValue(RealmClaim) == Realm;

    private AdministratorCredential? Read() =>
        !Provisioned ? null : JsonSerializer.Deserialize<AdministratorCredential>(File.ReadAllText(_path), JsonSerializerOptions.Web);
}
