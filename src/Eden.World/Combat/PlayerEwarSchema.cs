using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class PlayerEwarSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldPlayerEwar(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                TargetId INTEGER NOT NULL,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                SystemId INTEGER NOT NULL,TargetKind INTEGER NOT NULL,ContactJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldPlayerEwar_TargetId_TargetKind ON WorldPlayerEwar(TargetId,TargetKind);
            """
        );
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            foreach (string row in operation == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { operation == "DELETE" ? "OLD" : "NEW" })
            {
                string sql = $"""
                    CREATE TRIGGER IF NOT EXISTS Fit_WorldPlayerEwar_{operation}_{row} AFTER {operation} ON WorldPlayerEwar
                    BEGIN
                        INSERT INTO WorldFitRevision(PilotId,Stamp)
                        SELECT OwnerId,hex(randomblob(16)) FROM WorldShip WHERE Id={row}.TargetId
                        ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
                    END;
                    """;
                world.Database.ExecuteSqlRaw(sql);
            }
        }
    }
}
