using System.Globalization;
using Eden.Gameplay.Skills;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class WorldMigration
{
    internal const int CurrentVersion = 32;

    public static void Open(WorldDbContext world, CatalogDbContext catalog, TimeProvider clock)
    {
        CatalogSources.CheckBuild(catalog);
        using var transaction = world.Database.BeginTransaction();
        int version = world.Database.SqlQueryRaw<int>("PRAGMA user_version").AsEnumerable().Single();
        if (version is < 0 or > CurrentVersion)
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
        if (version is >= 6 and < 21)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldNpcAggression(NpcId INTEGER PRIMARY KEY REFERENCES WorldNpc(Id) ON DELETE CASCADE,
                    TargetShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE, ReadyAt INTEGER NOT NULL, LastShotAt INTEGER);
                CREATE INDEX IX_WorldNpcAggression_TargetShipId ON WorldNpcAggression(TargetShipId);
                CREATE TABLE WorldShipLoss(ShipId INTEGER PRIMARY KEY, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    NpcId INTEGER NOT NULL REFERENCES WorldNpc(Id) ON DELETE RESTRICT, CapsuleId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, Time INTEGER NOT NULL, Name TEXT NOT NULL, ItemsJson TEXT NOT NULL,
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL);
                CREATE INDEX IX_WorldShipLoss_PilotId_Time ON WorldShipLoss(PilotId,Time);
                """
            );
        }
        if (version < 21)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=21");
        }
        if (version is >= 6 and < 22)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldRepairPayment(Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, StationId INTEGER NOT NULL,
                    StationOwnerId INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL,
                    Time INTEGER NOT NULL, RepairsJson TEXT NOT NULL);
                CREATE INDEX IX_WorldRepairPayment_PilotId_Time ON WorldRepairPayment(PilotId,Time);
                """
            );
        }
        if (version < 22)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=22");
        }
        if (version is >= 6 and < 23)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE WorldWreck(Id INTEGER PRIMARY KEY,
                    NpcId INTEGER NOT NULL REFERENCES WorldNpc(Id) ON DELETE RESTRICT,
                    OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, CorporationId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, Name TEXT NOT NULL,
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                CREATE UNIQUE INDEX IX_WorldWreck_NpcId ON WorldWreck(NpcId);
                CREATE INDEX IX_WorldWreck_SystemId_ExpiresAt ON WorldWreck(SystemId,ExpiresAt);
                """
            );
        }
        if (version < 23)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=23");
        }
        if (version is >= 6 and < 24)
        {
            // Retain every existing NPC wreck and its item identities. Locations
            // are polymorphic inventory IDs, so no item FK needs rebuilding.
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldShipLoss ADD COLUMN DroppedItemIdsJson TEXT NOT NULL DEFAULT '[]';
                CREATE TABLE WorldWreckNew(Id INTEGER PRIMARY KEY,
                    NpcId INTEGER REFERENCES WorldNpc(Id) ON DELETE RESTRICT,
                    ShipId INTEGER REFERENCES WorldShipLoss(ShipId) ON DELETE RESTRICT,
                    OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE, CorporationId INTEGER NOT NULL,
                    TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, Name TEXT NOT NULL,
                    X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL,
                    CONSTRAINT CK_Wreck_Source CHECK ((NpcId IS NOT NULL AND ShipId IS NULL) OR (NpcId IS NULL AND ShipId IS NOT NULL)));
                INSERT INTO WorldWreckNew(Id,NpcId,OwnerId,CorporationId,TypeId,SystemId,Name,X,Y,Z,CreatedAt,ExpiresAt)
                    SELECT Id,NpcId,OwnerId,CorporationId,TypeId,SystemId,Name,X,Y,Z,CreatedAt,ExpiresAt FROM WorldWreck;
                DROP TABLE WorldWreck;
                ALTER TABLE WorldWreckNew RENAME TO WorldWreck;
                CREATE UNIQUE INDEX IX_WorldWreck_NpcId ON WorldWreck(NpcId);
                CREATE UNIQUE INDEX IX_WorldWreck_ShipId ON WorldWreck(ShipId);
                CREATE INDEX IX_WorldWreck_SystemId_ExpiresAt ON WorldWreck(SystemId,ExpiresAt);
                """
            );
        }
        if (version < 24)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=24");
        }
        if (version is >= 6 and < 25)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldWeaponCooldown ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0");
        }
        if (version < 25)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=25");
        }
        if (version is >= 6 and < 26)
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldNpc ADD COLUMN VelocityX REAL NOT NULL DEFAULT 0;
                ALTER TABLE WorldNpc ADD COLUMN VelocityY REAL NOT NULL DEFAULT 0;
                ALTER TABLE WorldNpc ADD COLUMN VelocityZ REAL NOT NULL DEFAULT 0;
                ALTER TABLE WorldNpc ADD COLUMN MotionAt INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE WorldNpc ADD COLUMN Navigation INTEGER NOT NULL DEFAULT 0;
                """
            );
        }
        if (version < 26)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=26");
        }
        if (version is >= 6 and < 27)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldIndustryJob(
                    Id INTEGER NOT NULL CONSTRAINT PK_WorldIndustryJob PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    BlueprintId INTEGER NOT NULL, BlueprintTypeId INTEGER NOT NULL, StationId INTEGER NOT NULL,
                    ProductTypeId INTEGER NOT NULL, Runs INTEGER NOT NULL, OutputQuantity INTEGER NOT NULL,
                    StartTime INTEGER NOT NULL, EndTime INTEGER NOT NULL, DurationSeconds INTEGER NOT NULL,
                    Status INTEGER NOT NULL, CompletedAt INTEGER, MaterialsJson TEXT NOT NULL,
                    CONSTRAINT CK_IndustryJob_Values CHECK (Runs > 0 AND OutputQuantity > 0 AND DurationSeconds > 0 AND EndTime >
                        StartTime));
                CREATE INDEX IF NOT EXISTS IX_WorldIndustryJob_PilotId_Status ON WorldIndustryJob(PilotId,Status);
                CREATE INDEX IF NOT EXISTS IX_WorldIndustryJob_BlueprintId ON WorldIndustryJob(BlueprintId);
                """
            );
        }
        if (version < 27)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=27");
        }
        if (version is >= 6 and < 28)
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldMarketStation ADD COLUMN SeedVersion INTEGER NOT NULL DEFAULT 0;
                CREATE TABLE WorldTestingGrant(
                    PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL,
                    CONSTRAINT CK_TestingGrant_Amounts CHECK (AmountCents > 0 AND BalanceCents >= 0));
                CREATE INDEX IX_WorldMarketOrder_RegionId_TypeId_Bid_PriceCents
                    ON WorldMarketOrder(RegionId,TypeId,Bid,PriceCents);
                """
            );
        }
        if (version < 28)
        {
            long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
            foreach (long pilotId in world.Pilots.Select(p => p.Id).ToArray())
            {
                TestingWalletFunding.Grant(world, pilotId, now);
            }
            world.Database.ExecuteSqlRaw("PRAGMA user_version=28");
        }
        if (version is >= 6 and < 29)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldArmorRepair(
                    ModuleId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    StartedAt INTEGER NOT NULL, CompletesAt INTEGER NOT NULL,
                    Amount REAL NOT NULL CONSTRAINT CK_ArmorRepair_Amount CHECK (Amount > 0));
                CREATE INDEX IF NOT EXISTS IX_WorldArmorRepair_ShipId_CompletesAt ON WorldArmorRepair(ShipId,CompletesAt);
                """
            );
        }
        if (version is >= 6 and < 30)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldActiveHardener(
                    ModuleId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE);
                CREATE INDEX IF NOT EXISTS IX_WorldActiveHardener_ShipId ON WorldActiveHardener(ShipId);
                """
            );
        }
        if (version is >= 6 and < 31)
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldActiveHardener RENAME TO WorldActiveModule;
                DROP INDEX IF EXISTS IX_WorldActiveHardener_ShipId;
                CREATE INDEX IX_WorldActiveModule_ShipId ON WorldActiveModule(ShipId);
                """
            );
        }
        if (version is >= 6 and < 32)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldItem ADD COLUMN CrystalDamage REAL NOT NULL DEFAULT 0");
        }
        if (version < CurrentVersion)
        {
            world.Database.ExecuteSqlRaw("PRAGMA user_version=32");
        }
        var skillTypes = world.Set<PilotSkill>().Select(s => s.TypeId).Distinct().ToArray();
        var rankedSkills = catalog
            .Set<ItemType>()
            .Where(t => skillTypes.Contains(t.Id) && t.Group.CategoryId == 16 && t.Attributes.Any(a => a.AttributeId == 275 && a.Value > 0))
            .Count();
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
                StartingSkills.Grant(world, catalog, pilot.Id, group.Key);
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
