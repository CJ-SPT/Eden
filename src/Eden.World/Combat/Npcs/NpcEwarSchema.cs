using Eden.Data.World.Combat.Npcs;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat.Npcs;

internal static class NpcEwarSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldNpcEwar(
                NpcId INTEGER NOT NULL REFERENCES WorldNpcAggression(NpcId) ON DELETE CASCADE,
                Kind INTEGER NOT NULL,TypeId INTEGER NOT NULL,
                TargetShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                StartedAt INTEGER NOT NULL,ReadyAt INTEGER NOT NULL,AbilityJson TEXT NOT NULL,AppliedJson TEXT,
                PRIMARY KEY(NpcId,Kind));
            CREATE INDEX IF NOT EXISTS IX_WorldNpcEwar_TargetShipId ON WorldNpcEwar(TargetShipId);
            CREATE TABLE IF NOT EXISTS WorldFitRevision(PilotId INTEGER PRIMARY KEY, Stamp TEXT NOT NULL);
            """
        );
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            foreach (string row in operation == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { operation == "DELETE" ? "OLD" : "NEW" })
            {
                string condition =
                    operation == "UPDATE"
                        ? "(OLD.AppliedJson IS NOT NEW.AppliedJson OR OLD.TargetShipId<>NEW.TargetShipId)"
                        : $"{row}.AppliedJson IS NOT NULL";
                string sql = $"""
                    CREATE TRIGGER IF NOT EXISTS Fit_WorldNpcEwar_{operation}_{row} AFTER {operation} ON WorldNpcEwar
                    WHEN {condition}
                    BEGIN
                        INSERT INTO WorldFitRevision(PilotId,Stamp)
                        SELECT OwnerId,hex(randomblob(16)) FROM WorldShip WHERE Id={row}.TargetShipId
                        ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
                    END;
                    """;
                world.Database.ExecuteSqlRaw(sql);
            }
        }
    }
}
