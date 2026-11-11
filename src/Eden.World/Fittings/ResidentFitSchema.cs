using Eden.Gameplay.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal static class ResidentFitSchema
{
    internal static void UpgradeShipChanges(WorldDbContext world)
    {
        // Station, storage and name changes do not alter the fitted graph. Keep both
        // owners invalidated when the hull identity, type or ownership changes.
        world.Database.ExecuteSqlRaw(
            """
            DROP TRIGGER IF EXISTS Fit_WorldShip_UPDATE_old;
            DROP TRIGGER IF EXISTS Fit_WorldShip_UPDATE_new;
            CREATE TRIGGER Fit_WorldShip_UPDATE_old AFTER UPDATE OF Id,TypeId,OwnerId ON WorldShip
            WHEN OLD.Id IS NOT NEW.Id OR OLD.TypeId IS NOT NEW.TypeId OR OLD.OwnerId IS NOT NEW.OwnerId
            BEGIN
                INSERT INTO WorldFitRevision(PilotId,Stamp) VALUES(OLD.OwnerId,hex(randomblob(16)))
                ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
            END;
            CREATE TRIGGER Fit_WorldShip_UPDATE_new AFTER UPDATE OF Id,TypeId,OwnerId ON WorldShip
            WHEN OLD.Id IS NOT NEW.Id OR OLD.TypeId IS NOT NEW.TypeId OR OLD.OwnerId IS NOT NEW.OwnerId
            BEGIN
                INSERT INTO WorldFitRevision(PilotId,Stamp) VALUES(NEW.OwnerId,hex(randomblob(16)))
                ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
            END;
            """
        );
    }

    internal static void Create(WorldDbContext world)
    {
        int lowSlot = (int)InventoryFlag.LowSlot0;
        int highSlot = (int)InventoryFlag.HighSlot7;
        world.Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS WorldFitRevision(PilotId INTEGER PRIMARY KEY, Stamp TEXT NOT NULL)");
        void Trigger(string table, string operation, string owner, string condition = "1")
        {
            string name = $"Fit_{table}_{operation.Split(' ')[0]}_{(owner.Contains("OLD", StringComparison.Ordinal) ? "old" : "new")}";
            // All SQL identifiers and clauses below come from this migration's fixed definitions.
            string sql = $"""
                CREATE TRIGGER IF NOT EXISTS {name} AFTER {operation} ON {table}
                BEGIN
                    INSERT INTO WorldFitRevision(PilotId,Stamp)
                    SELECT {owner},hex(randomblob(16)) WHERE {condition} AND {owner} IS NOT NULL
                    ON CONFLICT(PilotId) DO UPDATE SET Stamp=excluded.Stamp;
                END;
                """;
            world.Database.ExecuteSqlRaw(sql);
        }
        foreach (string table in new[] { "WorldPilotSkill", "WorldBooster", "WorldJumpClone", "WorldImplantState", "WorldBoosterState" })
        {
            foreach (string op in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                string row = op == "DELETE" ? "OLD" : "NEW";
                Trigger(table, op, row + ".PilotId");
            }
        }
        foreach (string op in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            foreach (string row in op == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { op == "DELETE" ? "OLD" : "NEW" })
            {
                string changed = op == "UPDATE" ? "UPDATE OF TypeId,OwnerId,LocationId,FlagId,Singleton,Online" : op;
                Trigger(
                    "WorldItem",
                    changed,
                    row + ".OwnerId",
                    $"({row}.FlagId BETWEEN {lowSlot} AND {highSlot} OR EXISTS(SELECT 1 FROM WorldItem m "
                        + $"WHERE m.Id={row}.LocationId AND m.FlagId BETWEEN {lowSlot} AND {highSlot}))"
                );
                Trigger("WorldShip", op, row + ".OwnerId");
                Trigger("WorldActiveModule", op, $"(SELECT OwnerId FROM WorldShip WHERE Id={row}.ShipId)");
                Trigger("WorldTackle", op, $"(SELECT OwnerId FROM WorldShip WHERE Id={row}.TargetId)");
                Trigger("WorldContractEscrow", op, $"(SELECT OwnerId FROM WorldItem WHERE Id={row}.ItemId)");
            }
        }
        Trigger("WorldPilot", "UPDATE", "NEW.Id");
    }
}
