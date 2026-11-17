using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Sovereignty;

internal static class SovereigntyActivitySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedSovereigntyActivity>().ToTable("WorldSovereigntyActivity").HasKey(row => row.SystemId);
        model.Entity<SavedSovereigntyActivity>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyCapital>().ToTable("WorldSovereigntyCapital").HasKey(row => row.AllianceId);
        model.Entity<SavedSovereigntyCapital>().Property(row => row.AllianceId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyActivityEvent>().ToTable("WorldSovereigntyActivityEvent").HasKey(row => row.Id);
        model.Entity<SavedSovereigntyActivityEvent>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntyActivityEvent>().HasIndex(row => row.SystemId);
        model.Entity<SavedSovereigntyCapitalChange>().ToTable("WorldSovereigntyCapitalChange").HasKey(row => row.Id);
        model.Entity<SavedSovereigntyCapitalChange>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntyCapitalChange>().HasIndex(row => row.AllianceId);
        model.Entity<SavedSovereigntyConflict>().ToTable("WorldSovereigntyConflict").HasKey(row => row.SystemId);
        model.Entity<SavedSovereigntyConflict>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyConflict>().HasIndex(row => row.HubId);
        model.Entity<SavedSovereigntyConflict>().HasIndex(row => row.ConstellationId);
        model.Entity<SavedSovereigntyConflict>().HasIndex(row => row.DefenderAllianceId);
    }

    internal static void Upgrade(WorldDbContext world, int version)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSovereigntyActivity(
                SystemId INTEGER PRIMARY KEY,
                MilitaryPoints REAL NOT NULL,
                IndustrialPoints REAL NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                LastMilitaryAt INTEGER NOT NULL,
                LastIndustrialAt INTEGER NOT NULL,
                Revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldSovereigntyCapital(
                AllianceId INTEGER PRIMARY KEY,
                CurrentSystemId INTEGER,
                NewSystemId INTEGER,
                ValidAfter INTEGER,
                PreviousSystemId INTEGER,
                CurrentSince INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldSovereigntyActivityEvent(
                Id INTEGER PRIMARY KEY,
                SystemId INTEGER NOT NULL,
                At INTEGER NOT NULL,
                Kind INTEGER NOT NULL,
                Amount REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyActivityEvent_SystemId ON WorldSovereigntyActivityEvent(SystemId);
            CREATE TABLE IF NOT EXISTS WorldSovereigntyCapitalChange(
                Id INTEGER PRIMARY KEY,
                AllianceId INTEGER NOT NULL,
                At INTEGER NOT NULL,
                SystemId INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyCapitalChange_AllianceId ON WorldSovereigntyCapitalChange(AllianceId);
            """
        );
        if (
            version < 182
            && !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldSovereigntyConflict')")
                .AsEnumerable()
                .Contains("DevelopmentAt")
        )
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN DevelopmentAt INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN AppliedAdm REAL NOT NULL DEFAULT 1;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN WindowAdm REAL NOT NULL DEFAULT 1;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN FollowingAdm REAL NOT NULL DEFAULT 1;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN WindowCenter INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN NextWindowLockAt INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN ScheduledPrimeHour INTEGER NOT NULL DEFAULT 18;
                ALTER TABLE WorldSovereigntyConflict ADD COLUMN CampaignAdm REAL NOT NULL DEFAULT 1;
                """
            );
        }
    }
}
