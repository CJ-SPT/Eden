using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class ProbeScanningSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedScanProbe>().ToTable("WorldScanProbe").HasKey(p => p.Id);
        model.Entity<SavedScanProbe>().HasIndex(p => new { p.PilotId, p.SystemId });
        model.Entity<SavedProbeScan>().ToTable("WorldProbeScan").HasKey(p => p.PilotId);
        model.Entity<SavedResolvedSignature>().ToTable("WorldResolvedSignature").HasKey(p => new { p.PilotId, p.SiteId });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldScanProbe (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                TypeId INTEGER NOT NULL, X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL,
                ExpiresAt INTEGER NOT NULL, ProfileJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldScanProbe_PilotId_SystemId ON WorldScanProbe(PilotId,SystemId);
            CREATE TABLE IF NOT EXISTS WorldProbeScan (
                PilotId INTEGER PRIMARY KEY, StartedAt INTEGER NOT NULL, ReadyAt INTEGER NOT NULL, ScanJson TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldResolvedSignature (
                PilotId INTEGER NOT NULL, SiteId INTEGER NOT NULL, SystemId INTEGER NOT NULL, PRIMARY KEY(PilotId,SiteId));
            """
        );
}
