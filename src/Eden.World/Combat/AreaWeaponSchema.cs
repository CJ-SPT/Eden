using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class AreaWeaponSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS SavedAreaWeapon(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL,SourceShipId INTEGER NOT NULL,ModuleId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL,ResolvesAt INTEGER NOT NULL,Snapshot TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_AreaWeapon_System ON SavedAreaWeapon(SystemId);
            CREATE INDEX IF NOT EXISTS IX_AreaWeapon_Module ON SavedAreaWeapon(ModuleId);
            """
        );
    }
}
