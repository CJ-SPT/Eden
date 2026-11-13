using Eden.Data.World.Characters;
using Eden.Data.World.Loot;
using Eden.Gameplay.Loot;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Loot;

internal static class JetcanSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedJetcan>().ToTable("WorldJetcan").HasKey(c => c.Id);
        model.Entity<SavedJetcan>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.OwnerId);
        model.Entity<SavedJetcan>().HasIndex(c => new { c.SystemId, c.ExpiresAt });
        model.Entity<SavedJettisonCooldown>().ToTable("WorldJettisonCooldown").HasKey(c => c.PilotId);
        model.Entity<SavedJettisonCooldown>().Property(c => c.PilotId).ValueGeneratedNever();
        model.Entity<SavedJettisonCooldown>().HasOne<Pilot>().WithMany().HasForeignKey(c => c.PilotId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSql(
            $"""
            CREATE TABLE IF NOT EXISTS WorldJetcan(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ShipId INTEGER NOT NULL, CorporationId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldJetcan_SystemId_ExpiresAt ON WorldJetcan(SystemId,ExpiresAt);
            INSERT INTO sqlite_sequence(name,seq)
                SELECT 'WorldJetcan',{JetcanRules.FirstId - 1}
                WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='WorldJetcan');
            CREATE TABLE IF NOT EXISTS WorldJettisonCooldown(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ReadyAt INTEGER NOT NULL);
            """
        );
}
