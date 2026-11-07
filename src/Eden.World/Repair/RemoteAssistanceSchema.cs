using Eden.Gameplay.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Repair;

internal static class RemoteAssistanceSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldRemoteAssistance(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                TargetId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                SystemId INTEGER NOT NULL,ContactJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldRemoteAssistance_TargetId ON WorldRemoteAssistance(TargetId);
            """
        );
        // Physical membership changes release the recipient in the committing
        // transaction, before either connection advances its next session frame.
        int firstMedium = (int)InventoryFlag.MediumSlot0;
        int lastMedium = (int)InventoryFlag.MediumSlot7;
        string membership = $"""
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_Item
            AFTER UPDATE OF TypeId,OwnerId,LocationId,FlagId,Singleton,Online ON WorldItem
            BEGIN
                DELETE FROM WorldRemoteAssistance WHERE ModuleId=NEW.Id AND (
                    NEW.TypeId<>OLD.TypeId OR NEW.OwnerId<>PilotId OR NEW.LocationId<>SourceShipId
                    OR NEW.FlagId NOT BETWEEN {firstMedium} AND {lastMedium} OR NEW.Singleton=0 OR NEW.Online=0);
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_Active AFTER DELETE ON WorldActiveModule
            BEGIN
                DELETE FROM WorldRemoteAssistance WHERE ModuleId=OLD.ModuleId;
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_Marker AFTER DELETE ON WorldRemoteAssistance
            BEGIN
                DELETE FROM WorldActiveModule WHERE ModuleId=OLD.ModuleId AND ShipId=OLD.SourceShipId;
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_Position AFTER DELETE ON WorldPosition
            BEGIN
                DELETE FROM WorldRemoteAssistance WHERE SourceShipId=OLD.ShipId OR TargetId=OLD.ShipId;
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_Selection AFTER UPDATE OF ActiveShipId ON WorldPilot
            WHEN NEW.ActiveShipId<>OLD.ActiveShipId
            BEGIN
                DELETE FROM WorldRemoteAssistance WHERE SourceShipId=OLD.ActiveShipId OR TargetId=OLD.ActiveShipId;
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_Owner AFTER UPDATE OF OwnerId ON WorldShip
            WHEN NEW.OwnerId<>OLD.OwnerId
            BEGIN
                DELETE FROM WorldRemoteAssistance WHERE SourceShipId=OLD.Id OR TargetId=OLD.Id;
            END;
            CREATE TRIGGER IF NOT EXISTS Clear_RemoteAssistance_System AFTER UPDATE OF SystemId ON WorldPosition
            WHEN NEW.SystemId IS NOT OLD.SystemId
            BEGIN
                DELETE FROM WorldRemoteAssistance WHERE (SourceShipId=NEW.ShipId OR TargetId=NEW.ShipId)
                    AND (NEW.SystemId IS NULL OR SystemId<>NEW.SystemId);
            END;
            """;
        world.Database.ExecuteSqlRaw(membership);
        foreach (string operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            foreach (string row in operation == "UPDATE" ? new[] { "OLD", "NEW" } : new[] { operation == "DELETE" ? "OLD" : "NEW" })
            {
                // Trigger identifiers come only from the fixed operation/row lists above.
                string sql = $"""
                    CREATE TRIGGER IF NOT EXISTS Fit_WorldRemoteAssistance_{operation}_{row} AFTER {operation} ON WorldRemoteAssistance
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
