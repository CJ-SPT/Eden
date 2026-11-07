using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class PilotCombatSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        if (
            world
                .Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_table_info('WorldShipLoss') WHERE name='AttackerPilotId'")
                .Single() != 0
        )
        {
            return;
        }
        // Keep wreck references and historical NPC losses while admitting a pilot attacker.
        world.Database.ExecuteSqlRaw(
            """
            PRAGMA defer_foreign_keys=ON;
            CREATE TABLE WorldShipLossNew(
                ShipId INTEGER PRIMARY KEY, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                NpcId INTEGER REFERENCES WorldNpc(Id) ON DELETE RESTRICT, AttackerPilotId INTEGER,
                CapsuleId INTEGER NOT NULL, TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, Time INTEGER NOT NULL,
                Name TEXT NOT NULL, ItemsJson TEXT NOT NULL, StoredShipsJson TEXT NOT NULL DEFAULT '[]',
                DroppedItemIdsJson TEXT NOT NULL DEFAULT '[]', X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL);
            INSERT INTO WorldShipLossNew
                (ShipId,PilotId,NpcId,CapsuleId,TypeId,SystemId,Time,Name,ItemsJson,StoredShipsJson,DroppedItemIdsJson,X,Y,Z)
                SELECT ShipId,PilotId,NpcId,CapsuleId,TypeId,SystemId,Time,Name,ItemsJson,StoredShipsJson,DroppedItemIdsJson,X,Y,Z
                FROM WorldShipLoss;
            DROP TABLE WorldShipLoss;
            ALTER TABLE WorldShipLossNew RENAME TO WorldShipLoss;
            CREATE INDEX IX_WorldShipLoss_PilotId_Time ON WorldShipLoss(PilotId,Time);
            """
        );
        if (world.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").Single() != 0)
        {
            throw new InvalidDataException("Ship-loss migration did not preserve foreign-key references.");
        }
        // SQLite retains a deferred violation for the dropped table even after
        // its replacement resolves every reference. Clear it only after checking all keys.
        world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=OFF");
    }
}
