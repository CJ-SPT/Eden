using Eden.Protocol;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Eden.Server.Launcher;

internal interface ILauncherPasswords
{
    string Hash(string name, string password);
    PasswordVerificationResult Verify(string name, string? hash, string password);
}

[Injectable(InjectionType.Singleton)]
internal sealed class LauncherPasswords : ILauncherPasswords
{
    private const int Iterations = 210000;
    private readonly PasswordHasher<string> _hasher = new(Options.Create(new PasswordHasherOptions { IterationCount = Iterations }));
    private readonly string _dummy;

    public LauncherPasswords()
    {
        _dummy = _hasher.HashPassword("", "invalid-user-dummy-password");
    }

    public string Hash(string name, string password) => _hasher.HashPassword(name, password);

    public PasswordVerificationResult Verify(string name, string? hash, string password) =>
        _hasher.VerifyHashedPassword(name, hash ?? _dummy, password);
}
