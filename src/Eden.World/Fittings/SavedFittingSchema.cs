using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal static class SavedFittingSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        var owners = world
            .Database.SqlQueryRaw<string>(
                "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldSavedFitting') WHERE \"from\"='OwnerId'"
            )
            .ToArray();
        if (owners is ["WorldPilot"])
        {
            long sequence = world
                .Database.SqlQueryRaw<long>("SELECT coalesce(max(seq),0) AS Value FROM sqlite_sequence WHERE name='WorldSavedFitting'")
                .Single();
            var objects = world
                .Database.SqlQueryRaw<string>(
                    "SELECT sql AS Value FROM sqlite_schema WHERE tbl_name='WorldSavedFitting' "
                        + "AND type IN ('index','trigger') AND sql IS NOT NULL"
                )
                .ToArray();
            world.Database.ExecuteSqlRaw(
                """
                DROP TRIGGER IF EXISTS DeletePilotSavedFittings;
                DROP TRIGGER IF EXISTS DeleteCorporationSavedFittings;
                CREATE TABLE WorldSavedFittingOwnershipUpgrade(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    OwnerId INTEGER NOT NULL REFERENCES WorldInventoryOwner(Id) ON DELETE CASCADE,
                    ShipTypeId INTEGER NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL,
                    ItemsJson TEXT NOT NULL, SavedAt INTEGER NOT NULL);
                INSERT INTO WorldSavedFittingOwnershipUpgrade SELECT * FROM WorldSavedFitting;
                DROP TABLE WorldSavedFitting;
                ALTER TABLE WorldSavedFittingOwnershipUpgrade RENAME TO WorldSavedFitting;
                """
            );
            foreach (string statement in objects)
            {
                world.Database.ExecuteSqlRaw(statement);
            }
            world.Database.ExecuteSql($"UPDATE sqlite_sequence SET seq=max(seq,{sequence}) WHERE name='WorldSavedFitting'");
        }
        else if (owners is not ["WorldInventoryOwner"])
        {
            throw new InvalidDataException("Unknown saved fitting owner constraint.");
        }
        // Inventory owners outlive deleted entities. Preserve personal fitting cleanup
        // and extend it to corporation templates without changing inventory lifetimes.
        world.Database.ExecuteSqlRaw(
            """
            CREATE TRIGGER IF NOT EXISTS DeletePilotSavedFittings AFTER DELETE ON WorldPilot
            BEGIN DELETE FROM WorldSavedFitting WHERE OwnerId=OLD.Id; END;
            CREATE TRIGGER IF NOT EXISTS DeleteCorporationSavedFittings AFTER DELETE ON WorldCorporation
            BEGIN DELETE FROM WorldSavedFitting WHERE OwnerId=OLD.Id; END;
            """
        );
    }

    internal static void Create(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSavedFitting(
                Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ShipTypeId INTEGER NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL,
                ItemsJson TEXT NOT NULL, SavedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSavedFitting_OwnerId ON WorldSavedFitting(OwnerId);
            """
        );
}
