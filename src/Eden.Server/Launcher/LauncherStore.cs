using System.Security.Cryptography;
using System.Text;
using Eden.Data.World.Launcher;
using Eden.Gameplay.Docking;
using Eden.Infrastructure.Registration;
using Eden.Protocol.PilotSessions;
using Eden.World.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

namespace Eden.Server.Launcher;

// Authentication tables extend the world without competing with gameplay's user_version migrations.
[Injectable(InjectionType.Singleton)]
internal sealed partial class LauncherStore(WorldDatabase database, TimeProvider clock, ILauncherPasswords? passwords = null)
{
    internal const int AccessSeconds = 15 * 60;
    internal const int RefreshSeconds = 30 * 24 * 60 * 60;
    internal const int TicketSeconds = 180;
    private const int MinimumPasswordLength = 6;
    private const int MaximumPasswordLength = 128;
    private readonly ILauncherPasswords _passwords = passwords ?? new LauncherPasswords();

    private long Now => clock.GetUtcNow().ToUnixTimeSeconds();

    public void Initialize()
    {
        using (var db = Open())
        {
            Execute(
                db,
                null,
                """
                CREATE TABLE IF NOT EXISTS LauncherCredential(
                    AccountId INTEGER PRIMARY KEY REFERENCES WorldAccount(Id), NormalizedName TEXT NOT NULL UNIQUE,
                    PasswordHash TEXT NOT NULL, Roles INTEGER NOT NULL);
                CREATE TABLE IF NOT EXISTS LauncherSession(
                    Id TEXT PRIMARY KEY, AccountId INTEGER NOT NULL REFERENCES WorldAccount(Id), AccessHash TEXT NOT NULL UNIQUE,
                    AccessExpires INTEGER NOT NULL, Expires INTEGER NOT NULL, Revoked INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS LauncherRefresh(
                    Hash TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES LauncherSession(Id), Used INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE IF NOT EXISTS LauncherTicket(
                    Hash TEXT PRIMARY KEY, SessionId TEXT NOT NULL REFERENCES LauncherSession(Id), Expires INTEGER NOT NULL);
                CREATE INDEX IF NOT EXISTS IX_LauncherSession_Revoked_Expires ON LauncherSession(Revoked,Expires);
                CREATE INDEX IF NOT EXISTS IX_LauncherRefresh_SessionId ON LauncherRefresh(SessionId);
                CREATE INDEX IF NOT EXISTS IX_LauncherTicket_SessionId ON LauncherTicket(SessionId);
                CREATE INDEX IF NOT EXISTS IX_LauncherTicket_Expires ON LauncherTicket(Expires);
                """
            );
        }
        Maintain();
    }

    public AccountReply Register(AccountRequest request)
    {
        string name = ValidateName(request.UserName);
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
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        if (Scalar(db, tx, "SELECT Id FROM WorldAccount WHERE upper(UserName)=$name", ("$name", name.ToUpperInvariant())) is not null)
        {
            throw new LauncherException("name_unavailable", "That account name is already in use.", 409);
        }
        long id = Convert.ToInt64(Scalar(db, tx, "SELECT MAX(COALESCE(MAX(Id),1000000),1000000)+1 FROM WorldAccount"));
        Execute(
            db,
            tx,
            "INSERT INTO WorldAccount(Id,UserName,CreatedAt,CloneGrade) VALUES($id,$name,$created,$grade)",
            ("$id", id),
            ("$name", name),
            ("$created", clock.GetUtcNow().UtcDateTime.ToFileTimeUtc()),
            ("$grade", (int)AccountCloneGrade.Omega)
        );
        Execute(
            db,
            tx,
            "INSERT INTO LauncherCredential VALUES($id,$name,$password,$roles)",
            ("$id", id),
            ("$name", name.ToUpperInvariant()),
            ("$password", passwordHash),
            ("$roles", roles)
        );
        tx.Commit();
        return new(id, name, request.Developer);
    }

    private LauncherCredential? ReadCredential(string userName)
    {
        using var db = Open();
        using var command = Command(
            db,
            null,
            """
            SELECT a.Id,a.UserName,c.PasswordHash FROM WorldAccount a
            JOIN LauncherCredential c ON c.AccountId=a.Id WHERE c.NormalizedName=$name
            """,
            ("$name", (userName ?? "").Trim().ToUpperInvariant())
        );
        using var reader = command.ExecuteReader();
        return reader.Read() ? new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)) : null;
    }

    public SessionReply SignIn(SignInRequest request)
    {
        // Expensive password work must never retain the resident world's connection lease.
        var credential = ReadCredential(request.UserName);
        var verified = request.Password is { Length: <= MaximumPasswordLength }
            ? _passwords.Verify(credential?.Name ?? "", credential?.Hash, request.Password)
            : PasswordVerificationResult.Failed;
        if (credential is null || verified == PasswordVerificationResult.Failed)
        {
            throw new LauncherException("invalid_credentials", "Invalid account name or password.", 401);
        }
        string? replacement =
            verified == PasswordVerificationResult.SuccessRehashNeeded ? _passwords.Hash(credential.Name, request.Password!) : null;
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var current = Scalar(db, tx, "SELECT PasswordHash FROM LauncherCredential WHERE AccountId=$id", ("$id", credential.Id));
        if (!string.Equals(current as string, credential.Hash, StringComparison.Ordinal))
        {
            throw new LauncherException("invalid_credentials", "Invalid account name or password.", 401);
        }
        if (replacement is not null)
        {
            Execute(
                db,
                tx,
                "UPDATE LauncherCredential SET PasswordHash=$hash WHERE AccountId=$id",
                ("$hash", replacement),
                ("$id", credential.Id)
            );
        }
        var result = IssueSession(db, tx, credential.Id, credential.Name, request.RememberMe);
        tx.Commit();
        return result;
    }

    private SessionReply IssueSession(SqliteConnection db, SqliteTransaction tx, long id, string name, bool remember)
    {
        string sessionId = Token();
        string access = Token();
        string? refresh = remember ? Token() : null;
        long expires = Now + AccessSeconds;
        Execute(
            db,
            tx,
            "INSERT INTO LauncherSession(Id,AccountId,AccessHash,AccessExpires,Expires) VALUES($s,$id,$a,$e,$r)",
            ("$s", sessionId),
            ("$id", id),
            ("$a", Hash(access)),
            ("$e", expires),
            ("$r", remember ? Now + RefreshSeconds : expires)
        );
        if (refresh is not null)
        {
            Execute(db, tx, "INSERT INTO LauncherRefresh(Hash,SessionId) VALUES($h,$s)", ("$h", Hash(refresh)), ("$s", sessionId));
        }
        long roles = Convert.ToInt64(Scalar(db, tx, "SELECT Roles FROM LauncherCredential WHERE AccountId=$id", ("$id", id)));
        return new(id, name, access, expires, refresh, roles == (long)PilotRole.Developer);
    }

    public SessionReply Refresh(string token)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using var command = Command(
            db,
            tx,
            """
            SELECT s.Id,s.AccountId,a.UserName,r.Used,s.Revoked,s.Expires FROM LauncherRefresh r
            JOIN LauncherSession s ON s.Id=r.SessionId JOIN WorldAccount a ON a.Id=s.AccountId WHERE r.Hash=$h
            """,
            ("$h", Hash(token))
        );
        string session;
        long id;
        string name;
        bool valid;
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                throw Unauthorized();
            }
            session = reader.GetString(0);
            id = reader.GetInt64(1);
            name = reader.GetString(2);
            valid = reader.GetInt64(3) == 0 && reader.GetInt64(4) == 0 && reader.GetInt64(5) > Now;
        }
        if (!valid)
        {
            Execute(db, tx, "UPDATE LauncherSession SET Revoked=1 WHERE Id=$s", ("$s", session));
            tx.Commit();
            throw Unauthorized();
        }
        string access = Token();
        string refresh = Token();
        Execute(db, tx, "UPDATE LauncherRefresh SET Used=1 WHERE Hash=$h", ("$h", Hash(token)));
        Execute(db, tx, "INSERT INTO LauncherRefresh(Hash,SessionId) VALUES($h,$s)", ("$h", Hash(refresh)), ("$s", session));
        Execute(
            db,
            tx,
            "UPDATE LauncherSession SET AccessHash=$h,AccessExpires=$e WHERE Id=$s",
            ("$h", Hash(access)),
            ("$e", Now + AccessSeconds),
            ("$s", session)
        );
        tx.Commit();
        return new(id, name, access, Now + AccessSeconds, refresh, ReadRoles(id) == (long)PilotRole.Developer);
    }

    public void Revoke(string access)
    {
        using var db = Open();
        Execute(db, null, "UPDATE LauncherSession SET Revoked=1 WHERE AccessHash=$h", ("$h", Hash(access)));
    }

    public string IssueTicket(string access)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var session =
            Scalar(
                db,
                tx,
                """
                SELECT Id FROM LauncherSession WHERE AccessHash=$h AND AccessExpires>$now AND Revoked=0 AND Expires>$now
                """,
                ("$h", Hash(access)),
                ("$now", Now)
            ) as string
            ?? throw Unauthorized();
        string ticket = "eden3." + Token();
        Execute(db, tx, "DELETE FROM LauncherTicket WHERE Expires<=$now", ("$now", Now));
        Execute(db, tx, "INSERT INTO LauncherTicket VALUES($h,$s,$e)", ("$h", Hash(ticket)), ("$s", session), ("$e", Now + TicketSeconds));
        tx.Commit();
        return ticket;
    }

    public long? ConsumeTicket(string ticket)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var id = Scalar(
            db,
            tx,
            """
            SELECT s.AccountId FROM LauncherTicket t JOIN LauncherSession s ON t.SessionId=s.Id
            WHERE t.Hash=$h AND t.Expires>$now AND s.Revoked=0 AND s.Expires>$now
            """,
            ("$h", Hash(ticket)),
            ("$now", Now)
        );
        Execute(db, tx, "DELETE FROM LauncherTicket WHERE Hash=$h", ("$h", Hash(ticket)));
        tx.Commit();
        return id is long account ? account : null;
    }

    public void SetPassword(string userName, string password)
    {
        ValidatePassword(password);
        string passwordHash = _passwords.Hash(userName, password);
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        var id =
            Scalar(
                db,
                tx,
                "SELECT Id FROM WorldAccount WHERE Id>0 AND upper(UserName)=$name",
                ("$name", userName.Trim().ToUpperInvariant())
            ) ?? throw new InvalidDataException("Unknown account.");
        Execute(
            db,
            tx,
            """
            INSERT INTO LauncherCredential VALUES($id,$name,$hash,$roles)
            ON CONFLICT(AccountId) DO UPDATE SET PasswordHash=excluded.PasswordHash,NormalizedName=excluded.NormalizedName
            """,
            ("$id", id),
            ("$name", userName.Trim().ToUpperInvariant()),
            ("$hash", passwordHash),
            ("$roles", (long)PilotRole.Player)
        );
        Execute(db, tx, "UPDATE LauncherSession SET Revoked=1 WHERE AccountId=$id", ("$id", id));
        tx.Commit();
    }

    private static string ValidateName(string value)
    {
        string name = (value ?? "").Trim();
        if (name.Length is < 3 or > 32 || name.Any(c => !char.IsAsciiLetterOrDigit(c) && !" ._-".Contains(c)))
        {
            throw new LauncherException("invalid_name", "Use 3–32 letters, numbers, spaces, dots, underscores or hyphens.");
        }
        return name;
    }

    public long ReadRoles(long accountId)
    {
        using var db = Open();
        if (Scalar(db, null, "SELECT name FROM sqlite_master WHERE type='table' AND name='LauncherCredential'") is null)
        {
            return (long)PilotRole.Player;
        }
        return Scalar(db, null, "SELECT Roles FROM LauncherCredential WHERE AccountId=$id", ("$id", accountId)) is long roles
            ? roles
            : (long)PilotRole.Player;
    }

    private static void ValidatePassword(string password)
    {
        if (password is not { Length: >= MinimumPasswordLength and <= MaximumPasswordLength })
        {
            throw new LauncherException(
                "invalid_password",
                $"Use a password containing {MinimumPasswordLength}–{MaximumPasswordLength} characters."
            );
        }
    }

    private SqliteConnection Open()
    {
        var db = WorldConnections.Create(database.Path);
        db.Open();
        return db;
    }

    private static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? "")));

    private static LauncherException Unauthorized() => new("unauthorized", "Please sign in again.", 401);

    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] values)
    {
        var command = db.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in values)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command;
    }

    private static object? Scalar(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] values)
    {
        using var command = Command(db, tx, sql, values);
        var value = command.ExecuteScalar();
        if (value is not null && !sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            WorldConnections.RequireDurableClose(db);
        }
        return value is DBNull ? null : value;
    }

    private static int Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string, object)[] values)
    {
        using var command = Command(db, tx, sql, values);
        int changed = command.ExecuteNonQuery();
        if (changed != 0 || sql.TrimStart().StartsWith("CREATE", StringComparison.OrdinalIgnoreCase))
        {
            WorldConnections.RequireDurableClose(db);
        }
        return changed;
    }
}
