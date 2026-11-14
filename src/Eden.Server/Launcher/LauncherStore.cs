using System.Security.Cryptography;
using System.Text;
using Eden.Data.World.Characters;
using Eden.Data.World.Launcher;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.AspNetCore.Identity;

namespace Eden.Server.Launcher;

[Injectable(InjectionType.Singleton)]
internal sealed partial class LauncherStore(WorldDatabase database, TimeProvider clock, ILauncherPasswords? passwords = null)
{
    internal const int AccessSeconds = 15 * 60;
    internal const int RefreshSeconds = 30 * 24 * 60 * 60;
    internal const int TicketSeconds = 180;
    private const int MinimumPasswordLength = 6;
    private const int MaximumPasswordLength = 128;
    private const long FirstAccountFloor = 1000000;
    private readonly ILauncherPasswords _passwords = passwords ?? new LauncherPasswords();

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public void Initialize() => Maintain();

    public AccountReply Register(AccountRequest request)
    {
        string name = ValidateName(request.UserName);
        string normalized = name.ToUpperInvariant();
        ValidatePassword(request.Password);
        long roles = (long)PilotRole.Player;
        if (request.Developer)
        {
            string? invitation = Environment.GetEnvironmentVariable("EDEN_DEVELOPER_INVITE_CODE");
            if (
                string.IsNullOrEmpty(invitation)
                || string.IsNullOrEmpty(request.DeveloperCode)
                || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(invitation)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(request.DeveloperCode))
                )
            )
            {
                throw new LauncherException("developer_code_required", "A valid server developer invitation code is required.", 403);
            }
            roles = (long)PilotRole.Developer;
        }
        string passwordHash = _passwords.Hash(name, request.Password);
        return Write(world =>
        {
            if (
                world.Filter<Account>(row => row.UserName.ToUpper() == normalized).Any()
                || world.Filter<SavedLauncherCredential>(row => row.NormalizedName == normalized).Any()
            )
            {
                throw new LauncherException("name_unavailable", "That account name is already in use.", 409);
            }
            long id = checked(Math.Max(world.Set<Account>().Select(row => (long?)row.Id).Max() ?? 0, FirstAccountFloor) + 1);
            world.Add(
                new Account
                {
                    Id = id,
                    UserName = name,
                    CloneGrade = AccountCloneGrade.Omega,
                    CreatedAt = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(),
                }
            );
            world.Add(
                new SavedLauncherCredential(id)
                {
                    NormalizedName = normalized,
                    PasswordHash = passwordHash,
                    Roles = roles,
                }
            );
            return new AccountReply(id, name, request.Developer);
        });
    }

    private LauncherCredential? ReadCredential(string userName)
    {
        string normalized = (userName ?? "").Trim().ToUpperInvariant();
        return WorldActionScope.Inspect(
            database,
            world =>
            {
                var row = world.Filter<SavedLauncherCredential>(credential => credential.NormalizedName == normalized).SingleOrDefault();
                var account = row is null ? null : world.Set<Account>().Find(row.AccountId);
                return row is not null && account is not null
                    ? new LauncherCredential(account.Id, account.UserName, row.PasswordHash)
                    : null;
            }
        );
    }

    public SessionReply SignIn(SignInRequest request)
    {
        // Password work is outside the world owner. Recheck the credential before issuing a session.
        var credential = ReadCredential(request.UserName);
        var verified = request.Password is { Length: <= MaximumPasswordLength }
            ? _passwords.Verify(credential?.Name ?? "", credential?.Hash, request.Password)
            : PasswordVerificationResult.Failed;
        if (credential is null || verified == PasswordVerificationResult.Failed)
        {
            throw InvalidCredentials();
        }
        string? replacement =
            verified == PasswordVerificationResult.SuccessRehashNeeded ? _passwords.Hash(credential.Name, request.Password!) : null;
        return Write(world =>
        {
            var current = world.Set<SavedLauncherCredential>().Find(credential.Id);
            if (current is null || !string.Equals(current.PasswordHash, credential.Hash, StringComparison.Ordinal))
            {
                throw InvalidCredentials();
            }
            current.PasswordHash = replacement ?? current.PasswordHash;
            long now = Now;
            string sessionId = Token();
            string access = Token();
            string? refresh = request.RememberMe ? Token() : null;
            long expires = now + AccessSeconds;
            world.Add(
                new SavedLauncherSession(sessionId)
                {
                    AccountId = credential.Id,
                    AccessHash = Hash(access),
                    AccessExpires = expires,
                    Expires = request.RememberMe ? now + RefreshSeconds : expires,
                }
            );
            if (refresh is not null)
            {
                world.Add(new SavedLauncherRefresh(Hash(refresh), sessionId));
            }
            return new SessionReply(credential.Id, credential.Name, access, expires, refresh, current.Roles == (long)PilotRole.Developer);
        });
    }

    public SessionReply Refresh(string token)
    {
        string hash = Hash(token);
        return Write<SessionReply?>(world =>
            {
                var refresh = world.Set<SavedLauncherRefresh>().Find(0, hash);
                var session = refresh is null ? null : world.Set<SavedLauncherSession>().Find(0, refresh.SessionId);
                var account = session is null ? null : world.Set<Account>().Find(session.AccountId);
                if (refresh is null || session is null || account is null)
                {
                    throw Unauthorized();
                }
                long now = Now;
                if (refresh.Used || session.Revoked || session.Expires <= now)
                {
                    session.Revoked = true;
                    return null;
                }
                string access = Token();
                string nextRefresh = Token();
                refresh.Used = true;
                session.AccessHash = Hash(access);
                session.AccessExpires = now + AccessSeconds;
                world.Add(new SavedLauncherRefresh(Hash(nextRefresh), session.Id));
                bool developer = world.Set<SavedLauncherCredential>().Find(account.Id)?.Roles == (long)PilotRole.Developer;
                return new(account.Id, account.UserName, access, session.AccessExpires, nextRefresh, developer);
            }) ?? throw Unauthorized();
    }

    public void Revoke(string access)
    {
        string hash = Hash(access);
        Write(world =>
        {
            if (world.Filter<SavedLauncherSession>(row => row.AccessHash == hash).SingleOrDefault() is { } session)
            {
                session.Revoked = true;
            }
            return true;
        });
    }

    private static SavedLauncherSession? Access(WorldAction world, string hash, long now) =>
        world
            .Filter<SavedLauncherSession>(row => row.AccessHash == hash && row.AccessExpires > now && !row.Revoked && row.Expires > now)
            .SingleOrDefault();

    public async Task<string> IssueTicketAsync(string access, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string ticket = "eden3." + Token();
        string hash = Hash(access);
        Commit(world =>
        {
            long now = Now;
            var session = Access(world, hash, now) ?? throw Unauthorized();
            world.RemoveRange(world.Filter<SavedLauncherTicket>(row => row.Expires <= now).ToArray());
            world.Add(new SavedLauncherTicket(Hash(ticket), session.Id, now + TicketSeconds));
            return true;
        });
        await Persist().WaitAsync(cancellationToken).ConfigureAwait(false);
        return ticket;
    }

    public void AuthorizeAccess(string access)
    {
        if (WorldActionScope.Inspect(database, world => Access(world, Hash(access), Now)) is null)
        {
            throw Unauthorized();
        }
    }

    public async Task<long?> ConsumeTicketAsync(string ticket, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string hash = Hash(ticket);
        var consumed = Commit(world =>
        {
            var saved = world.Set<SavedLauncherTicket>().Find(0, hash);
            if (saved is null)
            {
                return new TicketConsumption(false, null);
            }
            var session = world.Set<SavedLauncherSession>().Find(0, saved.SessionId);
            world.Remove(saved);
            long now = Now;
            return new TicketConsumption(
                true,
                saved.Expires > now && session is { Revoked: false } && session.Expires > now ? session.AccountId : null
            );
        });
        if (consumed.Removed)
        {
            await Persist().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        return consumed.AccountId;
    }

    private sealed record TicketConsumption(bool Removed, long? AccountId);

    public void SetPassword(string userName, string password)
    {
        ValidatePassword(password);
        string passwordHash = _passwords.Hash(userName, password);
        string normalized = userName.Trim().ToUpperInvariant();
        Write(world =>
        {
            long? id = world
                .Filter<Account>(row => row.Id > 0 && row.UserName.ToUpper() == normalized)
                .Select(row => (long?)row.Id)
                .SingleOrDefault();
            if (id is not long account)
            {
                throw new InvalidDataException("Unknown account.");
            }
            var credential =
                world.Set<SavedLauncherCredential>().Find(account)
                ?? world.Add(new SavedLauncherCredential(account) { Roles = (long)PilotRole.Player }).Entity;
            if (world.Filter<SavedLauncherCredential>(row => row.NormalizedName == normalized && row.AccountId != account).Any())
            {
                throw new InvalidDataException("The account name is already assigned to a launcher credential.");
            }
            credential.NormalizedName = normalized;
            credential.PasswordHash = passwordHash;
            foreach (var session in world.Filter<SavedLauncherSession>(row => row.AccountId == account))
            {
                session.Revoked = true;
            }
            return true;
        });
    }

    public long ReadRoles(long accountId) =>
        WorldActionScope.Inspect(database, world => world.Set<SavedLauncherCredential>().Find(accountId)?.Roles ?? (long)PilotRole.Player);

    private T Write<T>(Func<WorldAction, T> operation)
    {
        var result = Commit(operation);
        Persist().GetAwaiter().GetResult();
        return result;
    }

    private T Commit<T>(Func<WorldAction, T> operation)
    {
        using WorldActionScope scope = new(database, TransactionParticipants.None);
        var result = operation(scope.World);
        scope.Commit();
        return result;
    }

    private Task Persist() => LiveWorld.Find(database.Path)?.FlushAsync() ?? Task.CompletedTask;

    private static string ValidateName(string value)
    {
        string name = (value ?? "").Trim();
        if (name.Length is < 3 or > 32 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && !" ._-".Contains(c)))
        {
            throw new LauncherException("invalid_name", "Use 3–32 letters, numbers, spaces, dots, underscores or hyphens.");
        }
        return name;
    }

    private static void ValidatePassword(string password)
    {
        if (password is not { Length: >= MinimumPasswordLength and <= MaximumPasswordLength })
        {
            throw new LauncherException(
                "invalid_password",
                $"Use a password containing {MinimumPasswordLength}–{MaximumPasswordLength} characters.",
                400
            );
        }
    }

    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    private static LauncherException Unauthorized() => new("unauthorized", "Please sign in again.", 401);

    private static LauncherException InvalidCredentials() => new("invalid_credentials", "Invalid account name or password.", 401);
}
