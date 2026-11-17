using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Sovereignty;

internal static class SovereigntyWarfareSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedSovereigntyPrime>().ToTable("WorldSovereigntyPrime").HasKey(row => row.AllianceId);
        model.Entity<SavedSovereigntyPrime>().Property(row => row.AllianceId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyConflict>().ToTable("WorldSovereigntyConflict").HasKey(row => row.SystemId);
        model.Entity<SavedSovereigntyConflict>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyConflict>().HasIndex(row => row.HubId);
        model.Entity<SavedSovereigntyConflict>().HasIndex(row => row.ConstellationId);
        model.Entity<SavedSovereigntyConflict>().HasIndex(row => row.DefenderAllianceId);
        model.Entity<SavedSovereigntyNode>().ToTable("WorldSovereigntyNode").HasKey(row => row.Id);
        model.Entity<SavedSovereigntyNode>().Property(row => row.Id).ValueGeneratedNever();
        model.Entity<SavedSovereigntyNode>().HasIndex(row => row.SystemId);
        model.Entity<SavedSovereigntyNode>().HasIndex(row => row.SourceSystemId);
        model.Entity<SavedSovereigntyNode>().HasIndex(row => row.HubId);
        model.Entity<SavedSovereigntyNode>().HasIndex(row => row.AllianceId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSovereigntyPrime(
                AllianceId INTEGER PRIMARY KEY,
                CurrentHour INTEGER NOT NULL,
                NewHour INTEGER,
                ValidAfter INTEGER);
            CREATE TABLE IF NOT EXISTS WorldSovereigntyConflict(
                SystemId INTEGER PRIMARY KEY,
                HubId INTEGER NOT NULL,
                ConstellationId INTEGER NOT NULL,
                DefenderAllianceId INTEGER NOT NULL,
                Phase INTEGER NOT NULL,
                WindowStart INTEGER NOT NULL,
                WindowEnd INTEGER NOT NULL,
                CampaignAt INTEGER,
                DefenderPoints INTEGER NOT NULL,
                SecureUntil INTEGER NOT NULL,
                Sequence INTEGER NOT NULL,
                NextSpawnAt INTEGER NOT NULL,
                Value REAL NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                ScoringTeam INTEGER,
                Rate REAL NOT NULL,
                Blocked INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyConflict_HubId ON WorldSovereigntyConflict(HubId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyConflict_ConstellationId ON WorldSovereigntyConflict(ConstellationId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyConflict_DefenderAllianceId ON WorldSovereigntyConflict(DefenderAllianceId);
            CREATE TABLE IF NOT EXISTS WorldSovereigntyNode(
                Id INTEGER PRIMARY KEY,
                SystemId INTEGER NOT NULL,
                SourceSystemId INTEGER NOT NULL,
                HubId INTEGER NOT NULL,
                CorporationId INTEGER NOT NULL,
                AllianceId INTEGER NOT NULL,
                X REAL NOT NULL,
                Y REAL NOT NULL,
                Z REAL NOT NULL,
                Value REAL NOT NULL,
                UpdatedAt INTEGER NOT NULL,
                ScoringTeam INTEGER,
                Rate REAL NOT NULL,
                Blocked INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyNode_SystemId ON WorldSovereigntyNode(SystemId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyNode_SourceSystemId ON WorldSovereigntyNode(SourceSystemId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyNode_HubId ON WorldSovereigntyNode(HubId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyNode_AllianceId ON WorldSovereigntyNode(AllianceId);
            """
        );
}
