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
            CREATE TABLE IF NOT EXISTS SavedLanceDisruption(
                ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                EndsAt INTEGER NOT NULL
            );
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('SavedLanceDisruption')")
                .AsEnumerable()
                .Contains(nameof(Eden.Data.World.Combat.SavedLanceDisruption.OscillatorEndsAt))
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE SavedLanceDisruption ADD COLUMN OscillatorEndsAt INTEGER NOT NULL DEFAULT 0");
        }
    }
}
