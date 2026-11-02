using System.Globalization;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class WorldMigration
{
    public static void Open(WorldDbContext world, CatalogDbContext catalog)
    {
        CatalogSources.CheckBuild(catalog);
        using var transaction = world.Database.BeginTransaction();
        int version = world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
        if (version is < 0 or > 19)
        {
            throw new InvalidDataException($"Unsupported Eden world version {version}.");
        }
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
        if (version == 6)
        {
            // Old positions are relative to the ship's home system (also the null fallback).
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPosition ADD COLUMN SystemId INTEGER");
        }
        if (version is 6 or 7)
        {
            world.Database.ExecuteSqlRaw("""
                ALTER TABLE WorldAccount ADD COLUMN UserName TEXT NOT NULL DEFAULT 'Eden';
                ALTER TABLE WorldAccount ADD COLUMN CreatedAt INTEGER NOT NULL DEFAULT 134000000000000000;
                ALTER TABLE WorldPilot ADD COLUMN CreatedAt INTEGER NOT NULL DEFAULT 134000000000000000;
                ALTER TABLE WorldPilot ADD COLUMN RaceId INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldPilot ADD COLUMN BloodlineId INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldPilot ADD COLUMN GenderId INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldPilot ADD COLUMN AncestryId INTEGER NOT NULL DEFAULT 10;
                ALTER TABLE WorldPilot ADD COLUMN SchoolId INTEGER NOT NULL DEFAULT 19;
                """);
        }
        if (version < 8)
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldAccount SET UserName='Eden Wing' WHERE Id=1000002");
        }
        if (version is >= 6 and < 9)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldPilotSkill(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL,
                    Points INTEGER NOT NULL CONSTRAINT CK_PilotSkill_Points CHECK (Points >= 0),
                    PRIMARY KEY(PilotId,TypeId));
                """);
        }
        if (version < 10)
        {
            GrantStartingSkills(world, catalog);
        }
        if (version is >= 6 and < 11)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldSkillQueue(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, Position INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, Level INTEGER NOT NULL, StartPoints INTEGER NOT NULL, EndPoints INTEGER NOT NULL,
                    StartTime INTEGER, EndTime INTEGER, PRIMARY KEY(PilotId,Position));
                CREATE TABLE WorldSkillHistory(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Time INTEGER NOT NULL, TypeId INTEGER NOT NULL, Points INTEGER NOT NULL, EventId INTEGER NOT NULL);
                CREATE INDEX IX_WorldSkillHistory_PilotId_Time ON WorldSkillHistory(PilotId,Time);
                """);
        }
        if (version is >= 6 and < 12)
        {
            world.Database.ExecuteSqlRaw("""
                DROP INDEX IX_WorldPilot_AccountId;
                CREATE INDEX IX_WorldPilot_AccountId ON WorldPilot(AccountId);
                CREATE UNIQUE INDEX IX_WorldPilot_Name ON WorldPilot(Name COLLATE NOCASE);
                ALTER TABLE WorldPilot ADD COLUMN Appearance BLOB;
                ALTER TABLE WorldPilot ADD COLUMN Portrait BLOB;
                """);
        }
        if (version is >= 6 and < 13)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldItem(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL, LocationId INTEGER NOT NULL, FlagId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL CONSTRAINT CK_Item_Quantity CHECK (Quantity > 0));
                CREATE INDEX IX_WorldItem_OwnerId_LocationId ON WorldItem(OwnerId,LocationId);
                """);
        }
        if (version < 13)
        {
            foreach (var pilot in world.Pilots.Select(p => new { p.Id, p.ActiveShip.StationId }).ToArray())
            {
                PilotInventoryStore.GrantStarterEquipment(world, pilot.Id, pilot.StationId);
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=13");
        }
        if (version is >= 6 and < 14)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldItem ADD COLUMN Singleton INTEGER NOT NULL DEFAULT 0; ALTER TABLE WorldItem ADD COLUMN Online INTEGER NOT NULL DEFAULT 0");
        }
        if (version < 14)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=14");
        }
        if (version is >= 6 and < 15)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldAsteroid(
                    Id INTEGER PRIMARY KEY, BeltId INTEGER NOT NULL, SystemId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL CONSTRAINT CK_Asteroid_Quantity CHECK (Quantity >= 0),
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, Radius REAL NOT NULL);
                CREATE INDEX IX_WorldAsteroid_SystemId ON WorldAsteroid(SystemId);
                CREATE INDEX IX_WorldAsteroid_BeltId ON WorldAsteroid(BeltId);
                """);
        }
        if (version < 15)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=15");
        }
        if (version is >= 6 and < 16)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldShipEnergy(
                    ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    Charge REAL NOT NULL CONSTRAINT CK_ShipEnergy_Charge CHECK (Charge >= 0), UpdatedAt INTEGER NOT NULL);
                """);
        }
        if (version < 16) { world.Database.ExecuteSqlRaw("PRAGMA user_version=16"); }
        if (version is >= 6 and < 17)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldWallet(PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Cents INTEGER NOT NULL CONSTRAINT CK_Wallet_Cents CHECK(Cents >= 0));
                CREATE TABLE WorldMarketStation(StationId INTEGER PRIMARY KEY, CreatedAt INTEGER NOT NULL);
                CREATE TABLE WorldMarketOrder(Id INTEGER PRIMARY KEY AUTOINCREMENT, StationId INTEGER NOT NULL,
                    SystemId INTEGER NOT NULL, RegionId INTEGER NOT NULL, OwnerId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                    Bid INTEGER NOT NULL, PriceCents INTEGER NOT NULL CONSTRAINT CK_MarketOrder_Price CHECK(PriceCents > 0),
                    Remaining INTEGER NOT NULL, Entered INTEGER NOT NULL, Issued INTEGER NOT NULL,
                    CONSTRAINT CK_MarketOrder_Quantity CHECK(Remaining >= 0 AND Entered > 0 AND Remaining <= Entered));
                CREATE UNIQUE INDEX IX_WorldMarketOrder_StationId_TypeId_Bid ON WorldMarketOrder(StationId,TypeId,Bid);
                CREATE INDEX IX_WorldMarketOrder_RegionId ON WorldMarketOrder(RegionId);
                CREATE TABLE WorldMarketTrade(Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    OrderId INTEGER NOT NULL, StationId INTEGER NOT NULL, CounterpartyId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                    IsBuy INTEGER NOT NULL, Quantity INTEGER NOT NULL, PriceCents INTEGER NOT NULL, TaxCents INTEGER NOT NULL,
                    BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL,
                    CONSTRAINT CK_MarketTrade_Amounts CHECK(PriceCents > 0 AND Quantity > 0 AND TaxCents >= 0 AND BalanceCents >= 0));
                CREATE INDEX IX_WorldMarketTrade_PilotId_Time ON WorldMarketTrade(PilotId,Time);
                """);
        }
        if (version < 17) { world.Database.ExecuteSqlRaw("PRAGMA user_version=17"); }
        if (version is >= 6 and < 18)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldShipHealth(ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    ShieldCharge REAL NOT NULL, ArmorDamage REAL NOT NULL, StructureDamage REAL NOT NULL, UpdatedAt INTEGER NOT NULL,
                    CONSTRAINT CK_ShipHealth_Values CHECK(ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0));
                """);
        }
        if (version < 18) { world.Database.ExecuteSqlRaw("PRAGMA user_version=18"); }
        if (version is >= 6 and < 19)
        {
            world.Database.ExecuteSqlRaw("""
                CREATE TABLE WorldNpc(Id INTEGER PRIMARY KEY AUTOINCREMENT, BeltId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, ShieldCharge REAL NOT NULL,
                    ArmorDamage REAL NOT NULL, StructureDamage REAL NOT NULL, UpdatedAt INTEGER NOT NULL, DestroyedAt INTEGER,
                    CONSTRAINT CK_Npc_Health CHECK(ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0));
                CREATE INDEX IX_WorldNpc_BeltId ON WorldNpc(BeltId);
                CREATE INDEX IX_WorldNpc_SystemId ON WorldNpc(SystemId);
                """);
        }
        if (version < 19) { world.Database.ExecuteSqlRaw("PRAGMA user_version=19"); }
        var skillTypes = world.Set<PilotSkill>().Select(s => s.TypeId).Distinct().ToArray();
        var rankedSkills = catalog.Set<ItemType>().Where(t => skillTypes.Contains(t.Id) && t.Group.CategoryId == 16 &&
            t.Attributes.Any(a => a.AttributeId == 275 && a.Value > 0)).Count();
        if (rankedSkills != skillTypes.Length)
        {
            throw new InvalidDataException("Save references an unknown skill or training multiplier.");
        }
        foreach (int type in world.Ships.Select(s => s.TypeId).Distinct())
        {
            if (!catalog.Set<ItemType>().Any(t => t.Id == type))
            {
                throw new InvalidDataException($"Save references unknown ship type {type}.");
            }
        }
        foreach (int station in world.Ships.Select(s => s.StationId).Distinct())
        {
            if (!catalog.Set<Station>().Any(s => s.Id == station))
            {
                throw new InvalidDataException($"Save references unknown station {station}.");
            }
        }
        foreach (int corporation in world.Pilots.Select(p => p.CorporationId).Distinct())
        {
            if (!catalog.Set<NpcCorporation>().Any(c => c.Id == corporation))
            {
                throw new InvalidDataException($"Save references unknown pilot corporation {corporation}.");
            }
        }
        transaction.Commit();
    }

    private static void GrantStartingSkills(WorldDbContext world, CatalogDbContext catalog)
    {
        foreach (var group in world.Pilots.Select(p => new { p.Id, p.RaceId }).ToArray().GroupBy(p => p.RaceId))
        {
            foreach (var pilot in group)
            {
                GrantStartingSkills(world, catalog, pilot.Id, group.Key);
            }
        }
    }

    internal static void GrantStartingSkills(WorldDbContext world, CatalogDbContext catalog, long pilotId, int raceId)
    {
        var levels = CatalogData.Client.Races.Get(catalog, raceId).Skills
            ?? throw new InvalidDataException($"Missing starting skills for race {raceId}.");
        foreach (var (key, level) in levels)
        {
            int typeId = int.Parse(key, CultureInfo.InvariantCulture);
            double rank = catalog.Set<TypeAttribute>().Single(a => a.ItemTypeId == typeId && a.AttributeId == 275).Value;
            long points = TrainedSkill.PointsForLevel(rank, checked((int)level));
            world.Database.ExecuteSql($"""
                INSERT INTO WorldPilotSkill(PilotId,TypeId,Points) VALUES ({pilotId},{typeId},{points})
                ON CONFLICT(PilotId,TypeId) DO UPDATE SET Points=MAX(Points,excluded.Points)
                """);
        }
    }

    private static void CopyLegacy(WorldDbContext world, int version)
    {
        world.Database.ExecuteSqlRaw("INSERT INTO WorldAccount(Id) SELECT id FROM account");
        world.Database.ExecuteSqlRaw("""
            INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId)
            SELECT id,account,name,corporation,active_ship FROM pilot
            """);
        // Version 1 predates custom ship names. Every later version has the column.
        string copyShip = version == 1
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
        world.Database.ExecuteSql($"INSERT INTO WorldPilot(Id,AccountId,Name,CorporationId,ActiveShipId) VALUES ({pilot},{account},{pilotName},1000044,{ship})");
        world.Database.ExecuteSql($"INSERT INTO WorldShip(Id,OwnerId,TypeId,StationId,Name) VALUES ({ship},{pilot},601,60003760,{shipName})");
    }
}
