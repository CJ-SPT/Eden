using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Propulsion;

internal static class InterdictionSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS SavedInterdictionField(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                PilotId INTEGER NOT NULL,
                SourceShipId INTEGER NOT NULL,
                ModuleId INTEGER NOT NULL,
                TypeId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL,
                Kind INTEGER NOT NULL,
                X REAL NOT NULL,
                Y REAL NOT NULL,
                Z REAL NOT NULL,
                Radius REAL NOT NULL,
                HitPoints REAL NOT NULL,
                StartedAt INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_Interdiction_System ON SavedInterdictionField(SystemId);
            CREATE INDEX IF NOT EXISTS IX_Interdiction_Module ON SavedInterdictionField(ModuleId);
            """
        );
    }
}
