using Eden.Data.Combat;
using Eden.Gameplay.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class CrimewatchSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCrimewatch>().ToTable("WorldCrimewatch").HasKey(s => s.PilotId);
        model.Entity<SavedCrimewatch>().HasIndex(s => s.SystemId);
        model.Entity<SavedEngagement>().ToTable("WorldEngagement");
        model.Entity<SavedEngagement>().HasIndex(s => s.HigherPilotId);
        model.Entity<SavedLogoff>().ToTable("WorldLogoff").HasKey(s => s.PilotId);
        model.Entity<SavedLogoff>().HasIndex(s => new { s.SystemId, s.ExpiresAt });
        model.Entity<SavedPilotHostility>().ToTable("WorldPilotHostility");
        model.Entity<SavedPilotHostility>().HasIndex(h => new { h.TargetShipId, h.ExpiresAt });
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCrimewatch(
                PilotId INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL,
                WeaponsActive INTEGER NOT NULL, WeaponsUntil INTEGER NOT NULL,
                PvpActive INTEGER NOT NULL, PvpUntil INTEGER NOT NULL, NpcActive INTEGER NOT NULL, NpcUntil INTEGER NOT NULL,
                SuspectActive INTEGER NOT NULL, SuspectUntil INTEGER NOT NULL,
                CriminalActive INTEGER NOT NULL, CriminalUntil INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCrimewatch_SystemId ON WorldCrimewatch(SystemId);
            CREATE TABLE IF NOT EXISTS WorldEngagement(
                LowerPilotId INTEGER NOT NULL, HigherPilotId INTEGER NOT NULL, ActiveUntil INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                PRIMARY KEY(LowerPilotId,HigherPilotId));
            CREATE INDEX IF NOT EXISTS IX_WorldEngagement_HigherPilotId ON WorldEngagement(HigherPilotId);
            CREATE TABLE IF NOT EXISTS WorldLogoff(
                PilotId INTEGER PRIMARY KEY, ShipId INTEGER NOT NULL, SystemId INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldLogoff_SystemId_ExpiresAt ON WorldLogoff(SystemId,ExpiresAt);
            CREATE TABLE IF NOT EXISTS WorldPilotHostility(
                SourceShipId INTEGER NOT NULL, TargetShipId INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                PRIMARY KEY(SourceShipId,TargetShipId));
            CREATE INDEX IF NOT EXISTS IX_WorldPilotHostility_TargetShipId_ExpiresAt ON WorldPilotHostility(TargetShipId,ExpiresAt);
            """
        );
    }
}
