using Eden.Gameplay.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal static class RigFittingSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        int first = (int)InventoryFlag.RigSlot0;
        int last = (int)InventoryFlag.RigSlot2;
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            foreach (string row in operation == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { operation == "DELETE" ? "OLD" : "NEW" })
            {
                // Fixed migration identifiers only. Keep the existing module triggers;
                // these additional triggers cover rig insertion, removal and both owners.
                string name = $"Fit_WorldRig_{operation}_{row}";
                string change = operation == "UPDATE" ? "UPDATE OF TypeId,OwnerId,LocationId,FlagId,Singleton,Online" : operation;
                string sql = $"""
                    CREATE TRIGGER IF NOT EXISTS {name} AFTER {change} ON WorldItem
                    WHEN {row}.FlagId BETWEEN {first} AND {last}
                    BEGIN
                        INSERT INTO WorldFitRevision(PilotId,Stamp)
                        VALUES ({row}.OwnerId,hex(randomblob(16)))
                        ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
                    END;
                    """;
                world.Database.ExecuteSqlRaw(sql);
            }
        }
    }
}
