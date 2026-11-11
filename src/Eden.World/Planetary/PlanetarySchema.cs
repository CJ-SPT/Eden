using Eden.Data.World.Characters;
using Eden.Data.World.Planetary;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Planetary;

internal static class PlanetarySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedPlanetaryColony>().ToTable("WorldPlanetaryColony").HasKey(c => new { c.PilotId, c.PlanetId });
        model.Entity<SavedPlanetaryColony>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
        model.Entity<SavedPlanetaryColony>().HasIndex(c => c.PlanetId);
        model.Entity<SavedPlanetaryIdentity>().ToTable("WorldPlanetaryIdentity").HasKey(i => i.Id);
        model.Entity<SavedPlanetaryIdentity>().Property(i => i.Id).ValueGeneratedNever();
        model.Entity<SavedPlanetaryLaunch>().ToTable("WorldPlanetaryLaunch").HasKey(l => l.Id);
        model.Entity<SavedPlanetaryLaunch>().HasOne<Pilot>().WithMany().HasForeignKey(l => l.PilotId);
        model.Entity<SavedPlanetaryLaunch>().HasIndex(l => new { l.SystemId, l.ExpiresAt });
        model.Entity<SavedPlanetaryLaunch>().HasIndex(l => l.PilotId);
        model.Entity<SavedPlanetaryOffice>().ToTable("WorldPlanetaryOffice").HasKey(o => o.PlanetId);
        model.Entity<SavedPlanetaryOffice>().Property(o => o.PlanetId).ValueGeneratedNever();
        model.Entity<SavedPlanetaryOffice>().HasIndex(o => o.SystemId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldPlanetaryColony(
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                PlanetId INTEGER NOT NULL, StateJson TEXT NOT NULL, LastEditHash TEXT NOT NULL, LastEditAt INTEGER NOT NULL,
                PRIMARY KEY(PilotId,PlanetId));
            CREATE INDEX IF NOT EXISTS IX_WorldPlanetaryColony_PlanetId ON WorldPlanetaryColony(PlanetId);
            CREATE TABLE IF NOT EXISTS WorldPlanetaryIdentity(Id INTEGER PRIMARY KEY, NextPinId INTEGER NOT NULL);
            INSERT OR IGNORE INTO WorldPlanetaryIdentity VALUES(1,18000000000000);
            CREATE TABLE IF NOT EXISTS WorldPlanetaryLaunch(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                PlanetId INTEGER NOT NULL, SystemId INTEGER NOT NULL, CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL);
            INSERT OR IGNORE INTO sqlite_sequence(name,seq)
                SELECT 'WorldPlanetaryLaunch',1700000000 WHERE NOT EXISTS(
                    SELECT 1 FROM sqlite_sequence WHERE name='WorldPlanetaryLaunch');
            CREATE INDEX IF NOT EXISTS IX_WorldPlanetaryLaunch_SystemId_ExpiresAt ON WorldPlanetaryLaunch(SystemId,ExpiresAt);
            CREATE INDEX IF NOT EXISTS IX_WorldPlanetaryLaunch_PilotId ON WorldPlanetaryLaunch(PilotId);
            CREATE TABLE IF NOT EXISTS WorldPlanetaryOffice(
                PlanetId INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldPlanetaryOffice_SystemId ON WorldPlanetaryOffice(SystemId);
            """
        );
}
