using Eden.Gameplay.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationStorageSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationOffice(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE RESTRICT,
                StationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, RentedAt INTEGER NOT NULL,
                FeeCents INTEGER NOT NULL CHECK(FeeCents>=0));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporationOffice_CorporationId_StationId
                ON WorldCorporationOffice(CorporationId,StationId);
            CREATE TABLE IF NOT EXISTS WorldCorporationOfficeSettings(
                Id INTEGER PRIMARY KEY CHECK(Id=1), RentalFeeCents INTEGER NOT NULL CHECK(RentalFeeCents>=0),
                OfficeSlots INTEGER NOT NULL CHECK(OfficeSlots>0));
            """
        );
        world.Database.ExecuteSql(
            $"""
            INSERT OR IGNORE INTO WorldCorporationOfficeSettings(Id,RentalFeeCents,OfficeSlots)
                VALUES(1,{CorporationStorageRules.DefaultRentalFeeCents},{CorporationStorageRules.DefaultOfficeSlots})
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationChange')")
                .Contains("StorageJson")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporationChange ADD COLUMN StorageJson TEXT");
        }
    }
}
