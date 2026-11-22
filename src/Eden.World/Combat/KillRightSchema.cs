using Eden.Data.World.Characters;
using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class KillRightSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var rights = model.Entity<SavedKillRight>().ToTable("WorldKillRight");
        rights.HasOne<Pilot>().WithMany().HasForeignKey(row => row.OwnerId);
        rights.HasOne<Pilot>().WithMany().HasForeignKey(row => row.TargetId);
        rights.HasIndex(row => new { row.OwnerId, row.TargetId }).IsUnique();
        rights.HasIndex(row => row.TargetId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldKillRight(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                TargetId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ExpiresAt INTEGER NOT NULL, PriceCents INTEGER, RestrictedTo INTEGER, ActiveUntil INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldKillRight_OwnerId_TargetId ON WorldKillRight(OwnerId, TargetId);
            CREATE INDEX IF NOT EXISTS IX_WorldKillRight_TargetId ON WorldKillRight(TargetId);
            """
        );
}
