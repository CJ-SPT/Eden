using Eden.Data.World.Mining;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Mining;

internal static class MiningSiteSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedMiningSite>().ToTable("WorldMiningSite").HasKey(s => s.Id);
        model.Entity<SavedMiningSite>().HasIndex(s => new { s.SystemId, s.Family });
        model.Entity<SavedMiningDeposit>().ToTable("WorldMiningDeposit").HasKey(d => d.Id);
        model.Entity<SavedMiningDeposit>().HasIndex(d => d.SiteId);
        model
            .Entity<SavedMiningDeposit>()
            .HasOne<SavedMiningSite>()
            .WithMany()
            .HasForeignKey(d => d.SiteId)
            .OnDelete(DeleteBehavior.Cascade);
        model.Entity<SavedMiningDeposit>().HasOne<WorldAsteroid>().WithMany().HasForeignKey(d => d.Id).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldMiningSite (
                Id INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, Family INTEGER NOT NULL, DungeonId INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, ExpiresAt INTEGER NOT NULL, ReplaceAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldMiningSite_SystemId_Family ON WorldMiningSite(SystemId,Family);
            CREATE TABLE IF NOT EXISTS WorldMiningDeposit (
                Id INTEGER PRIMARY KEY REFERENCES WorldAsteroid(Id) ON DELETE CASCADE,
                SiteId INTEGER NOT NULL REFERENCES WorldMiningSite(Id) ON DELETE CASCADE);
            CREATE INDEX IF NOT EXISTS IX_WorldMiningDeposit_SiteId ON WorldMiningDeposit(SiteId);
            """
        );
}
