using Eden.Data.World.Inventory;
using Eden.Data.World.Propulsion;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

internal static class SpatialNavigationSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SpatialNavigationState>().ToTable("WorldSpatialNavigation").HasKey(s => s.ShipId);
        model.Entity<SpatialNavigationState>().HasOne<Ship>().WithMany().HasForeignKey(s => s.ShipId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSpatialNavigation(
                ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                TargetingReadyAt INTEGER NOT NULL DEFAULT 0,
                CloakModuleId INTEGER NOT NULL DEFAULT 0,
                CloakTargetingMilliseconds REAL NOT NULL DEFAULT 0, CloakReactivationMilliseconds REAL NOT NULL DEFAULT 0,
                JumpModuleId INTEGER NOT NULL DEFAULT 0, JumpStartedAt INTEGER NOT NULL DEFAULT 0,
                JumpEndsAt INTEGER NOT NULL DEFAULT 0,
                HeadingX REAL NOT NULL DEFAULT 0, HeadingY REAL NOT NULL DEFAULT 0, HeadingZ REAL NOT NULL DEFAULT 0,
                VelocityX REAL NOT NULL DEFAULT 0, VelocityY REAL NOT NULL DEFAULT 0, VelocityZ REAL NOT NULL DEFAULT 0);
            """
        );
}
