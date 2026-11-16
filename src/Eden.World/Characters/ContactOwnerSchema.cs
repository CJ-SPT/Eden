using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

internal static class ContactOwnerSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        var target = world
            .Database.SqlQueryRaw<string>(
                "SELECT \"table\" AS Value FROM pragma_foreign_key_list('WorldContact') WHERE \"from\"='ContactId'"
            )
            .Single();
        if (target == "WorldPilot")
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP TRIGGER IF EXISTS DeletePilotContacts;
                DROP TRIGGER IF EXISTS DeleteCorporationContacts;
                CREATE TABLE WorldContactOwnerUpgrade(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    ContactId INTEGER NOT NULL REFERENCES WorldInventoryOwner(Id) ON DELETE CASCADE,
                    Standing REAL NOT NULL, InWatchlist INTEGER NOT NULL, LabelMask INTEGER NOT NULL,
                    PRIMARY KEY(PilotId,ContactId));
                INSERT INTO WorldContactOwnerUpgrade SELECT * FROM WorldContact;
                DROP TABLE WorldContact;
                ALTER TABLE WorldContactOwnerUpgrade RENAME TO WorldContact;
                CREATE INDEX IX_WorldContact_ContactId ON WorldContact(ContactId);
                """
            );
        }
        else if (target != "WorldInventoryOwner")
        {
            throw new InvalidDataException("Unknown personal contact target constraint.");
        }
        // Inventory identities outlive their entities; retain historical character cleanup.
        world.Database.ExecuteSqlRaw(
            """
            CREATE TRIGGER IF NOT EXISTS DeletePilotContacts AFTER DELETE ON WorldPilot
            BEGIN DELETE FROM WorldContact WHERE ContactId=OLD.Id; END;
            CREATE TRIGGER IF NOT EXISTS DeleteCorporationContacts AFTER DELETE ON WorldCorporation
            BEGIN DELETE FROM WorldContact WHERE ContactId=OLD.Id; END;
            """
        );
    }
}
