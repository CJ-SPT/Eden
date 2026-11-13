using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fleets;

internal static class CommandBurstSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCommandBurst(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                SystemId INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL,
                BoostsJson TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_WorldCommandBurst_ShipId ON WorldCommandBurst(ShipId);
            CREATE INDEX IF NOT EXISTS IX_WorldCommandBurst_SystemExpiry ON WorldCommandBurst(SystemId,ExpiresAt);
            CREATE INDEX IF NOT EXISTS IX_WorldCommandBurst_ExpiresAt ON WorldCommandBurst(ExpiresAt);
            CREATE TRIGGER IF NOT EXISTS Clear_CommandBurst_Position AFTER DELETE ON WorldPosition
            BEGIN
                DELETE FROM WorldCommandBurst WHERE ShipId=OLD.ShipId;
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_CommandBurst_System AFTER UPDATE OF SystemId ON WorldPosition
            WHEN OLD.SystemId IS NOT NEW.SystemId
            BEGIN
                DELETE FROM WorldCommandBurst WHERE ShipId=NEW.ShipId AND SystemId IS NOT NEW.SystemId;
            END;
            """
        );
        foreach (string operation in new[] { "INSERT", "DELETE" })
        {
            string row = operation == "INSERT" ? "NEW" : "OLD";
            // The two trigger identifiers and row aliases are fixed above.
            string sql = $"""
                CREATE TRIGGER IF NOT EXISTS Fit_WorldCommandBurst_{operation} AFTER {operation} ON WorldCommandBurst
                BEGIN
                    INSERT INTO WorldFitRevision(PilotId,Stamp)
                    SELECT OwnerId,hex(randomblob(16)) FROM WorldShip WHERE Id={row}.ShipId
                    ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
                END;
                """;
            world.Database.ExecuteSqlRaw(sql);
        }
    }
}
