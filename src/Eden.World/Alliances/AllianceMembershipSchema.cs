using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Alliances;

internal static class AllianceMembershipSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAllianceApplication>().ToTable("WorldAllianceApplication").HasKey(row => row.Id);
        model.Entity<SavedAllianceApplication>().HasIndex(row => row.CorporationId);
        model.Entity<SavedAllianceApplication>().HasIndex(row => row.AllianceId);
        model.Entity<SavedAllianceApplication>().HasIndex(row => new { row.AllianceId, row.CorporationId }).IsUnique();
        model.Entity<SavedAllianceHistory>().ToTable("WorldAllianceHistory").HasKey(row => row.Id);
        model.Entity<SavedAllianceHistory>().HasIndex(row => row.CorporationId);
        model.Entity<SavedAllianceChange>().ToTable("WorldAllianceChange").HasKey(row => row.Id);
        model.Entity<SavedAllianceChange>().HasIndex(row => row.AllianceId);
        model.Entity<SavedAllianceChange>().HasIndex(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAllianceApplication(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, CorporationId INTEGER NOT NULL, AllianceId INTEGER NOT NULL,
                Text TEXT NOT NULL, Status INTEGER NOT NULL, AppliedAt INTEGER NOT NULL, EffectiveAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceApplication_CorporationId ON WorldAllianceApplication(CorporationId);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceApplication_AllianceId ON WorldAllianceApplication(AllianceId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldAllianceApplication_AllianceId_CorporationId
                ON WorldAllianceApplication(AllianceId,CorporationId);
            CREATE TABLE IF NOT EXISTS WorldAllianceHistory(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, CorporationId INTEGER NOT NULL, AllianceId INTEGER, StartedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceHistory_CorporationId ON WorldAllianceHistory(CorporationId);
            CREATE TABLE IF NOT EXISTS WorldAllianceChange(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, AllianceId INTEGER NOT NULL, CorporationId INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceChange_AllianceId ON WorldAllianceChange(AllianceId);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceChange_CorporationId ON WorldAllianceChange(CorporationId);
            """
        );
}
