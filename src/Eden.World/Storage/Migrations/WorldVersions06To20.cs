using Eden.Data.World.Combat.Npcs;
using Eden.Data.World.Mining;
using Eden.World.Catalog;
using Eden.World.Combat.Npcs;
using Eden.World.Contracts;
using Eden.World.Inventory;
using Eden.World.Mining;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersions06To20
{
    internal static void Apply(WorldDbContext world, CatalogDbContext catalog, int version)
    {
        if (version is >= 6 and < 50)
        {
            ContractPersistence.CreateSchema(world);
        }
        if (version == 6)
        {
            // Old positions are relative to the ship's home system (also the null fallback).
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldPosition ADD COLUMN SystemId INTEGER");
        }
        if (version is 6 or 7)
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldAccount ADD COLUMN UserName TEXT NOT NULL DEFAULT 'Eden';
                ALTER TABLE WorldAccount ADD COLUMN CreatedAt INTEGER NOT NULL DEFAULT 134000000000000000;
                ALTER TABLE WorldPilot ADD COLUMN CreatedAt INTEGER NOT NULL DEFAULT 134000000000000000;
                ALTER TABLE WorldPilot ADD COLUMN RaceId INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldPilot ADD COLUMN BloodlineId INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldPilot ADD COLUMN GenderId INTEGER NOT NULL DEFAULT 1;
                ALTER TABLE WorldPilot ADD COLUMN AncestryId INTEGER NOT NULL DEFAULT 10;
                ALTER TABLE WorldPilot ADD COLUMN SchoolId INTEGER NOT NULL DEFAULT 19;
                """
            );
        }
        if (version < 8)
        {
            world.Database.ExecuteSqlRaw("UPDATE WorldAccount SET UserName='Eden Wing' WHERE Id=1000002");
        }
        if (version is >= 6 and < 9)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldPilotSkill(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL,
                    Points INTEGER NOT NULL CONSTRAINT CK_PilotSkill_Points CHECK (Points >= 0),
                    PRIMARY KEY(PilotId,TypeId));
                """
            );
        }
        if (version < 10)
        {
            GrantStartingSkills(world, catalog);
        }
        if (version is >= 6 and < 11)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldSkillQueue(
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, Position INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, Level INTEGER NOT NULL, StartPoints INTEGER NOT NULL, EndPoints INTEGER NOT NULL,
                    StartTime INTEGER, EndTime INTEGER, PRIMARY KEY(PilotId,Position));
                CREATE TABLE WorldSkillHistory(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    Time INTEGER NOT NULL, TypeId INTEGER NOT NULL, Points INTEGER NOT NULL, EventId INTEGER NOT NULL);
                CREATE INDEX IX_WorldSkillHistory_PilotId_Time ON WorldSkillHistory(PilotId,Time);
                """
            );
        }
        if (version is >= 6 and < 12)
        {
            world.Database.ExecuteSqlRaw(
                """
                DROP INDEX IX_WorldPilot_AccountId;
                CREATE INDEX IX_WorldPilot_AccountId ON WorldPilot(AccountId);
                CREATE UNIQUE INDEX IX_WorldPilot_Name ON WorldPilot(Name COLLATE NOCASE);
                ALTER TABLE WorldPilot ADD COLUMN Appearance BLOB;
                ALTER TABLE WorldPilot ADD COLUMN Portrait BLOB;
                """
            );
        }
        if (version is >= 6 and < 13)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldItem(
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    TypeId INTEGER NOT NULL, LocationId INTEGER NOT NULL, FlagId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL CONSTRAINT CK_Item_Quantity CHECK (Quantity > 0));
                CREATE INDEX IX_WorldItem_OwnerId_LocationId ON WorldItem(OwnerId,LocationId);
                """
            );
        }
        if (version < 13)
        {
            foreach (var pilot in world.Pilots.Select(p => new { p.Id, p.ActiveShip.StationId }).ToArray())
            {
                PilotInventoryStore.GrantStarterMiner(world, pilot.Id, pilot.StationId);
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=13");
        }
        if (version is >= 6 and < 14)
        {
            world.Database.ExecuteSqlRaw(
                "ALTER TABLE WorldItem ADD COLUMN Singleton INTEGER NOT NULL DEFAULT 0; ALTER TABLE WorldItem ADD "
                    + "COLUMN Online INTEGER NOT NULL DEFAULT 0"
            );
        }
        if (version < 14)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=14");
        }
        if (version is >= 6 and < 15)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldAsteroid(
                    Id INTEGER PRIMARY KEY, BeltId INTEGER NOT NULL, SystemId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                    Quantity INTEGER NOT NULL CONSTRAINT CK_Asteroid_Quantity CHECK (Quantity >= 0),
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, Radius REAL NOT NULL);
                CREATE INDEX IX_WorldAsteroid_SystemId ON WorldAsteroid(SystemId);
                CREATE INDEX IX_WorldAsteroid_BeltId ON WorldAsteroid(BeltId);
                """
            );
        }
        if (version < 15)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=15");
        }
        if (version is >= 6 and < 16)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldShipEnergy(
                    ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    Charge REAL NOT NULL CONSTRAINT CK_ShipEnergy_Charge CHECK (Charge >= 0), UpdatedAt INTEGER NOT NULL);
                """
            );
        }
        if (version < 16)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=16");
        }
        if (version is >= 6 and < 17)
        {
            world.Database.ExecuteSqlRaw(
                """
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
                CREATE TABLE WorldMarketTrade(Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id)
                    ON DELETE CASCADE,
                    OrderId INTEGER NOT NULL, StationId INTEGER NOT NULL, CounterpartyId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                    IsBuy INTEGER NOT NULL, Quantity INTEGER NOT NULL, PriceCents INTEGER NOT NULL, TaxCents INTEGER NOT NULL,
                    BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL,
                    CONSTRAINT CK_MarketTrade_Amounts CHECK(PriceCents > 0 AND Quantity > 0 AND TaxCents >= 0 AND BalanceCents >= 0));
                CREATE INDEX IX_WorldMarketTrade_PilotId_Time ON WorldMarketTrade(PilotId,Time);
                """
            );
        }
        if (version < 17)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=17");
        }
        if (version is >= 6 and < 18)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldShipHealth(ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    ShieldCharge REAL NOT NULL, ArmorDamage REAL NOT NULL, StructureDamage REAL NOT NULL, UpdatedAt INTEGER NOT NULL,
                    CONSTRAINT CK_ShipHealth_Values CHECK(ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0));
                """
            );
        }
        if (version < 18)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=18");
        }
        if (version is >= 6 and < 19)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldNpc(Id INTEGER PRIMARY KEY AUTOINCREMENT, BeltId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, ShieldCharge REAL NOT NULL,
                    ArmorDamage REAL NOT NULL, StructureDamage REAL NOT NULL, UpdatedAt INTEGER NOT NULL, DestroyedAt INTEGER,
                    CONSTRAINT CK_Npc_Health CHECK(ShieldCharge >= 0 AND ArmorDamage >= 0 AND StructureDamage >= 0));
                CREATE INDEX IX_WorldNpc_BeltId ON WorldNpc(BeltId);
                CREATE INDEX IX_WorldNpc_SystemId ON WorldNpc(SystemId);
                """
            );
        }
        if (version < 19)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=19");
        }
        if (version is >= 6 and < 20)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldWeaponCooldown(ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE, ReadyAt
                    INTEGER NOT NULL);
                CREATE TABLE WorldNpcBounty(Id INTEGER PRIMARY KEY AUTOINCREMENT, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON
                    DELETE CASCADE,
                    NpcId INTEGER NOT NULL REFERENCES WorldNpc(Id) ON DELETE CASCADE, TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                    Time INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL,
                    CONSTRAINT CK_NpcBounty_Amounts CHECK(AmountCents > 0 AND BalanceCents >= 0));
                CREATE UNIQUE INDEX IX_WorldNpcBounty_NpcId ON WorldNpcBounty(NpcId);
                CREATE INDEX IX_WorldNpcBounty_PilotId_Time ON WorldNpcBounty(PilotId,Time);
                """
            );
        }
        if (version < 20)
        {
            foreach (var pilot in world.Pilots.Select(p => new { p.Id, p.ActiveShip.StationId }).ToArray())
            {
                PilotInventoryStore.GrantStarterWeapon(world, pilot.Id, pilot.StationId);
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=20");
        }
    }

    private static void GrantStartingSkills(WorldDbContext world, CatalogDbContext catalog)
    {
        foreach (var group in world.Pilots.Select(p => new { p.Id, p.RaceId }).ToArray().GroupBy(p => p.RaceId))
        {
            foreach (var pilot in group)
            {
                StartingSkills.Grant(world, catalog, pilot.Id, group.Key);
            }
        }
    }
}
