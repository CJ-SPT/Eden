using Eden.Data.World.Characters;
using Eden.Data.World.Launcher;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage;

internal static class LauncherSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedLauncherCredential>().ToTable("LauncherCredential").HasKey(row => row.AccountId);
        model.Entity<SavedLauncherCredential>().HasIndex(row => row.NormalizedName).IsUnique();
        model
            .Entity<SavedLauncherCredential>()
            .HasOne<Account>()
            .WithMany()
            .HasForeignKey(row => row.AccountId)
            .OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedLauncherSession>().ToTable("LauncherSession").HasKey(row => row.Id);
        model.Entity<SavedLauncherSession>().HasIndex(row => row.AccessHash).IsUnique();
        model.Entity<SavedLauncherSession>().HasIndex(row => new { row.Revoked, row.Expires });
        model
            .Entity<SavedLauncherSession>()
            .HasOne<Account>()
            .WithMany()
            .HasForeignKey(row => row.AccountId)
            .OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedLauncherRefresh>().ToTable("LauncherRefresh").HasKey(row => row.Hash);
        model
            .Entity<SavedLauncherRefresh>()
            .HasOne<SavedLauncherSession>()
            .WithMany()
            .HasForeignKey(row => row.SessionId)
            .OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedLauncherTicket>().ToTable("LauncherTicket").HasKey(row => row.Hash);
        model
            .Entity<SavedLauncherTicket>()
            .HasOne<SavedLauncherSession>()
            .WithMany()
            .HasForeignKey(row => row.SessionId)
            .OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedLauncherTicket>().HasIndex(row => row.Expires);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
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
