using Eden.World.Characters;
using Eden.World.Combat;
using Eden.World.Drones;
using Eden.World.Fittings;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersions69To84
{
    internal static void Apply(WorldDbContext world, int version)
    {
        if (version < 69)
        {
            ResidentFitSchema.Create(world);
            world.Database.ExecuteSqlRaw("PRAGMA user_version=69");
        }
        if (version < 70)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldWeaponLink(
                    SlaveId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    MasterId INTEGER NOT NULL REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    CHECK(SlaveId<>MasterId));
                CREATE INDEX IF NOT EXISTS IX_WorldWeaponLink_MasterId ON WorldWeaponLink(MasterId);
                CREATE INDEX IF NOT EXISTS IX_WorldWeaponLink_ShipId ON WorldWeaponLink(ShipId);
                PRAGMA user_version=70;
                """
            );
        }
        PilotCombatSchema.Upgrade(world);
        PilotMissileSchema.Upgrade(world);
        if (version < 71)
        {
            InteractionHistorySchema.Upgrade(world);
            world.Database.ExecuteSqlRaw("PRAGMA user_version=71");
        }
        if (version < 78)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=77");
        }
        if (version < 84)
        {
            SavedFittingSchema.Create(world);
        }
        // A current-version save may predate the tables on another branch.
        // Reconcile the idempotent drone schema before startup reads it.
        DroneSchema.Upgrade(world);
    }
}
