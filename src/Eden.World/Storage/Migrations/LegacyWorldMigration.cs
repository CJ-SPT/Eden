using Eden.Data.Characters;
using Eden.World.Characters;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class LegacyWorldMigration
{
    internal static void Initialize(WorldDbContext world, int version)
    {
        if (version < 6)
        {
            world.Database.ExecuteSqlRaw("PRAGMA defer_foreign_keys=ON");
            world.Database.ExecuteSqlRaw(world.Database.GenerateCreateScript());
            if (version > 0)
            {
                CopyLegacy(world, version);
            }
            if (version == 0)
            {
                Seed(world, 1000001, 90000001, 1000000000001, "Eden Pilot", "Ibis");
            }
            if (version < 5)
            {
                Seed(world, 1000002, 90000002, 1000000000002, "Eden Wing", "Wing Ibis");
            }
        }
    }

    private static void CopyLegacy(WorldDbContext world, int version)
    {
        world.Database.ExecuteSqlRaw("INSERT INTO WorldAccount(Id) SELECT id FROM account");
        world.Database.ExecuteSqlRaw(
            """
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId)
            SELECT id,account,name,corporation,active_ship FROM pilot
            """
        );
        // Version 1 predates custom ship names. Every later version has the column.
        string copyShip =
            version == 1
                ? "INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) SELECT id,owner,type,station,'Ibis' FROM ship"
                : "INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) SELECT id,owner,type,station,name FROM ship";
        world.Database.ExecuteSqlRaw(copyShip);
        if (version >= 4)
        {
            world.Database.ExecuteSqlRaw("INSERT INTO WorldPosition(ShipId,X,Y,Z) SELECT ship,x,y,z FROM flight; DROP TABLE flight;");
        }
        world.Database.ExecuteSqlRaw("DROP TABLE pilot; DROP TABLE ship; DROP TABLE station; DROP TABLE ship_type; DROP TABLE account;");
    }

    private static void Seed(WorldDbContext world, long account, long pilot, long ship, string pilotName, string shipName)
    {
        // The active ship references its owner and the owner references the ship.
        // Both constraints are checked together when this transaction commits.
        world.Database.ExecuteSql($"INSERT INTO WorldAccount(Id) VALUES ({account})");
        world.Database.ExecuteSql(
            $"INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId) VALUES ({pilot},{account},{pilotName},1000044,{ship})"
        );
        world.Database.ExecuteSql(
            $"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{pilot},601,60003760,{shipName})"
        );
    }
}
