using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Skills;
using Eden.Protocol.Skills;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class WorldMigration
{
    internal const int CurrentVersion = 77;

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
        if (version is >= 6 and < 33)
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldArmorRepair ADD COLUMN Layer INTEGER NOT NULL DEFAULT 0");
        }
        if (version is >= 6 and < 34)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldMissile(
                    Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                    PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                    SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    ModuleId INTEGER NOT NULL, LauncherTypeId INTEGER NOT NULL, ChargeTypeId INTEGER NOT NULL,
                    GroupId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                    TargetId INTEGER NOT NULL REFERENCES WorldNpc(Id) ON DELETE CASCADE, TargetRadius REAL NOT NULL,
                    CorporationId INTEGER NOT NULL, LaunchStamp INTEGER NOT NULL,
                    LaunchedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL, EndedAt INTEGER,
                    Status INTEGER NOT NULL, Reported INTEGER NOT NULL, Snapshot TEXT NOT NULL, Outcome TEXT);
                CREATE INDEX IF NOT EXISTS IX_WorldMissile_SystemId_Status_UpdatedAt ON WorldMissile(SystemId,Status,UpdatedAt);
                CREATE INDEX IF NOT EXISTS IX_WorldMissile_PilotId_Reported ON WorldMissile(PilotId,Reported);
                """
            );
        }
        if (version < 34)
        {
            // Keep the projectile identity range separate from ships, owned
            // items, NPCs and wrecks; AUTOINCREMENT preserves it after cleanup.
            long sequence = MissileDefinitions.FirstProjectileId - 1;
            world.Database.ExecuteSql($"UPDATE sqlite_sequence SET seq=MAX(seq,{sequence}) WHERE name='WorldMissile'");
            world.Database.ExecuteSql(
                $"""
                INSERT INTO sqlite_sequence(name,seq)
                    SELECT 'WorldMissile',{sequence} WHERE NOT EXISTS(SELECT 1 FROM sqlite_sequence WHERE name='WorldMissile');
                """
            );
        }
        if (version < CurrentVersion)
        {
            if (version is >= 6 and < 35)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldTackle(
                        ModuleId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        SourceShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                        TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL, TargetId INTEGER NOT NULL, TargetKind INTEGER NOT NULL,
                        Effect INTEGER NOT NULL, DurationMilliseconds REAL NOT NULL, CapacitorNeed REAL NOT NULL,
                        Range REAL NOT NULL, Strength REAL NOT NULL, WebPercent REAL NOT NULL, MwdBlock REAL NOT NULL,
                        StartedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldTackle_TargetId_TargetKind ON WorldTackle(TargetId,TargetKind);
                    """
                );
            }
            if (version is >= 6 and < 36)
            {
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldPilot ADD COLUMN SafetyLevel INTEGER NOT NULL DEFAULT 2");
            }
            if (version is >= 6 and < 37)
            {
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldAccount ADD COLUMN CloneGrade INTEGER NOT NULL DEFAULT 0");
            }
            if (version is >= 6 and < 38)
            {
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldShip ADD COLUMN StoredInId INTEGER NULL");
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldShip ADD COLUMN StorageFlagId INTEGER NOT NULL DEFAULT 4");
                world.Database.ExecuteSqlRaw("CREATE INDEX IX_WorldShip_OwnerId_StoredInId ON WorldShip(OwnerId,StoredInId)");
                world.Database.ExecuteSqlRaw("ALTER TABLE WorldShipLoss ADD COLUMN StoredShipsJson TEXT NOT NULL DEFAULT '[]'");
            }
            if (version is >= 6 and < 39)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldCharacterSheet(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Biography TEXT NOT NULL, Intelligence INTEGER NOT NULL, Memory INTEGER NOT NULL,
                        Charisma INTEGER NOT NULL, Perception INTEGER NOT NULL, Willpower INTEGER NOT NULL,
                        BonusRemaps INTEGER NOT NULL, NextTimedRemap INTEGER);
                    """
                );
            }
            if (version is >= 6 and < 40)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldCharacterSetting(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Key TEXT NOT NULL, Text TEXT, Binary BLOB, PRIMARY KEY(PilotId, Key),
                        CONSTRAINT CK_CharacterSetting_Value CHECK ((Text IS NULL) <> (Binary IS NULL)));
                    """
                );
            }
            if (version is >= 6 and < 41)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldContact(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ContactId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Standing REAL NOT NULL, InWatchlist INTEGER NOT NULL, LabelMask INTEGER NOT NULL,
                        PRIMARY KEY(PilotId, ContactId));
                    CREATE TABLE IF NOT EXISTS WorldBlockedContact(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ContactId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        PRIMARY KEY(PilotId, ContactId));
                    CREATE TABLE IF NOT EXISTS WorldContactLabel(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Id INTEGER NOT NULL, Name TEXT NOT NULL, Color INTEGER, PRIMARY KEY(PilotId, Id));
                    CREATE INDEX IF NOT EXISTS IX_WorldContact_ContactId ON WorldContact(ContactId);
                    CREATE INDEX IF NOT EXISTS IX_WorldBlockedContact_ContactId ON WorldBlockedContact(ContactId);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldContactLabel_PilotId_Name ON WorldContactLabel(PilotId, Name);
                    """
                );
            }
            if (version is >= 6 and < 42)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldBookmarkFolder(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Name TEXT NOT NULL, Description TEXT NOT NULL, IsActive INTEGER NOT NULL, IsKnown INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldBookmarkFolder_PilotId ON WorldBookmarkFolder(PilotId);
                    CREATE TABLE IF NOT EXISTS WorldBookmarkSubfolder(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        FolderId INTEGER NOT NULL REFERENCES WorldBookmarkFolder(Id) ON DELETE CASCADE, Name TEXT NOT NULL,
                        CONSTRAINT AK_WorldBookmarkSubfolder_Id_FolderId UNIQUE(Id, FolderId));
                    CREATE INDEX IF NOT EXISTS IX_WorldBookmarkSubfolder_FolderId ON WorldBookmarkSubfolder(FolderId);
                    CREATE TABLE IF NOT EXISTS WorldBookmark(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        FolderId INTEGER NOT NULL REFERENCES WorldBookmarkFolder(Id) ON DELETE CASCADE,
                        SubfolderId INTEGER, CreatorId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ItemId INTEGER, TypeId INTEGER NOT NULL, LocationId INTEGER NOT NULL,
                        X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL,
                        Name TEXT NOT NULL, Note TEXT NOT NULL, CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER,
                        FOREIGN KEY(SubfolderId, FolderId) REFERENCES WorldBookmarkSubfolder(Id, FolderId) ON DELETE RESTRICT);
                    CREATE INDEX IF NOT EXISTS IX_WorldBookmark_FolderId ON WorldBookmark(FolderId);
                    CREATE INDEX IF NOT EXISTS IX_WorldBookmark_CreatorId ON WorldBookmark(CreatorId);
                    CREATE INDEX IF NOT EXISTS IX_WorldBookmark_SubfolderId_FolderId ON WorldBookmark(SubfolderId, FolderId);
                    """
                );
            }
            if (version is >= 6 and < 43)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldInsurancePolicy(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        ShipId INTEGER NOT NULL, PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        TypeId INTEGER NOT NULL, Level INTEGER NOT NULL, ValueCents INTEGER NOT NULL, PremiumCents INTEGER NOT NULL,
                        StartTime INTEGER NOT NULL, EndTime INTEGER NOT NULL, Status INTEGER NOT NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldInsurancePolicy_ShipId ON WorldInsurancePolicy(ShipId) WHERE Status=0;
                    CREATE INDEX IF NOT EXISTS IX_WorldInsurancePolicy_PilotId ON WorldInsurancePolicy(PilotId);
                    CREATE TABLE IF NOT EXISTS WorldInsuranceLedger(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ShipId INTEGER NOT NULL, TypeId INTEGER NOT NULL,
                        PolicyId INTEGER REFERENCES WorldInsurancePolicy(Id) ON DELETE RESTRICT,
                        IsPayout INTEGER NOT NULL, AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_ShipId ON WorldInsuranceLedger(ShipId) WHERE IsPayout=1;
                    CREATE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_PilotId ON WorldInsuranceLedger(PilotId);
                    CREATE INDEX IF NOT EXISTS IX_WorldInsuranceLedger_PolicyId ON WorldInsuranceLedger(PolicyId);
                    """
                );
            }
            if (version is >= 6 and < 44)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldTaskCounter(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Name TEXT NOT NULL, Count INTEGER NOT NULL, PRIMARY KEY(PilotId, Name));
                    CREATE TABLE IF NOT EXISTS WorldCompletedTask(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Id INTEGER NOT NULL, Time INTEGER NOT NULL, PRIMARY KEY(PilotId, Id));
                    """
                );
            }
            if (version is >= 6 and < 45)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldHomeStation(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, StationId INTEGER NOT NULL);
                    CREATE TABLE IF NOT EXISTS WorldHomeStationPayment(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        StationId INTEGER NOT NULL, OperatorId INTEGER NOT NULL, AmountCents INTEGER NOT NULL,
                        BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldHomeStationPayment_PilotId ON WorldHomeStationPayment(PilotId);
                    CREATE TABLE IF NOT EXISTS WorldHomeCloneTransfer(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        OldShipId INTEGER NOT NULL, NewShipId INTEGER NOT NULL,
                        SourceStationId INTEGER NOT NULL, DestinationStationId INTEGER NOT NULL, Time INTEGER NOT NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldHomeCloneTransfer_OldShipId ON WorldHomeCloneTransfer(OldShipId);
                    CREATE INDEX IF NOT EXISTS IX_WorldHomeCloneTransfer_PilotId ON WorldHomeCloneTransfer(PilotId);
                    """
                );
            }
            if (version is >= 6 and < 46)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldAgentContact(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        AgentId INTEGER NOT NULL, PRIMARY KEY(PilotId, AgentId));
                    """
                );
            }
            if (version is >= 6 and < 47)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldJumpClone(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Active INTEGER NOT NULL, StationId INTEGER, Name TEXT NOT NULL,
                        CONSTRAINT CK_JumpClone_Location CHECK (
                            (Active = 1 AND StationId IS NULL) OR (Active = 0 AND StationId IS NOT NULL)));
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldJumpClone_PilotId ON WorldJumpClone(PilotId) WHERE Active = 1;
                    CREATE TABLE IF NOT EXISTS WorldJumpCloneTimer(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Time INTEGER NOT NULL);
                    CREATE TABLE IF NOT EXISTS WorldJumpClonePayment(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        StationId INTEGER NOT NULL, OperatorId INTEGER NOT NULL, Kind INTEGER NOT NULL,
                        AmountCents INTEGER NOT NULL, BalanceCents INTEGER NOT NULL, Time INTEGER NOT NULL,
                        CloneId INTEGER NOT NULL, DestinationStationId INTEGER, OldShipId INTEGER, NewShipId INTEGER);
                    CREATE INDEX IF NOT EXISTS IX_WorldJumpClonePayment_PilotId ON WorldJumpClonePayment(PilotId);
                    """
                );
            }
            if (version is >= 6 and < 48)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldCalendarEvent(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        OwnerId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Time INTEGER NOT NULL, DurationMinutes INTEGER, Title TEXT NOT NULL, Description TEXT NOT NULL,
                        Important INTEGER NOT NULL, Deleted INTEGER NOT NULL, Modified INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldCalendarEvent_OwnerId ON WorldCalendarEvent(OwnerId);
                    CREATE INDEX IF NOT EXISTS IX_WorldCalendarEvent_Time ON WorldCalendarEvent(Time);
                    CREATE TABLE IF NOT EXISTS WorldCalendarParticipant(
                        EventId INTEGER NOT NULL REFERENCES WorldCalendarEvent(Id) ON DELETE CASCADE,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Response INTEGER NOT NULL, PRIMARY KEY(EventId,PilotId));
                    CREATE INDEX IF NOT EXISTS IX_WorldCalendarParticipant_PilotId ON WorldCalendarParticipant(PilotId);
                    CREATE TABLE IF NOT EXISTS WorldCalendarChange(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        EventId INTEGER NOT NULL REFERENCES WorldCalendarEvent(Id) ON DELETE CASCADE,
                        Kind INTEGER NOT NULL, OwnerId INTEGER NOT NULL, Time INTEGER NOT NULL, OldTime INTEGER NOT NULL,
                        DurationMinutes INTEGER, Title TEXT NOT NULL, Important INTEGER NOT NULL,
                        Deleted INTEGER NOT NULL, Modified INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldCalendarChange_EventId ON WorldCalendarChange(EventId);
                    CREATE INDEX IF NOT EXISTS IX_WorldCalendarChange_PilotId_Id ON WorldCalendarChange(PilotId,Id);
                    """
                );
            }
            if (version is >= 6 and < 49)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldInboxNotification(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, Type INTEGER NOT NULL, SenderId INTEGER NOT NULL,
                        ReceiverId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Processed INTEGER NOT NULL, Deleted INTEGER NOT NULL, Created INTEGER NOT NULL,
                        ContactLevel REAL NOT NULL, Message TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldInboxNotification_ReceiverId_Id ON WorldInboxNotification(ReceiverId,Id);
                    """
                );
            }
            if (version is >= 6 and < 51)
            {
                ContractPersistence.UpgradeCourier(world);
            }
            if (version is >= 6 and < 52)
            {
                ContractPersistence.UpgradeAuctions(world);
            }
            if (version is >= 6 and < 53)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldShipDirt(
                        ShipId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                        Timestamp INTEGER NOT NULL);
                    """
                );
            }
            if (version is >= 6 and < 54)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldPilotSecurity(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Value REAL NOT NULL);
                    CREATE TABLE IF NOT EXISTS WorldSecurityPending(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Due INTEGER NOT NULL, NpcId INTEGER NOT NULL, TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                        CorporationId INTEGER NOT NULL, Fraction REAL NOT NULL, Maximum REAL NOT NULL);
                    CREATE TABLE IF NOT EXISTS WorldSecurityTransaction(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Time INTEGER NOT NULL, NpcId INTEGER NOT NULL, TypeId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                        CorporationId INTEGER NOT NULL, Modification REAL NOT NULL, NewValue REAL NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldSecurityTransaction_PilotId_Id ON WorldSecurityTransaction(PilotId,Id);
                    """
                );
            }
            if (version is >= 6 and < 55)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldRedeemToken(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        AccountId INTEGER NOT NULL REFERENCES WorldAccount(Id) ON DELETE CASCADE,
                        Reference TEXT NOT NULL, TypeId INTEGER NOT NULL, Quantity INTEGER NOT NULL,
                        Label TEXT NOT NULL, Description TEXT NOT NULL, StationId INTEGER NULL,
                        CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NULL, State INTEGER NOT NULL,
                        FinishedAt INTEGER NULL, PilotId INTEGER NULL, ItemId INTEGER NULL, ReversedAt INTEGER NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldRedeemToken_AccountId_Reference ON WorldRedeemToken(AccountId,Reference);
                    """
                );
            }
            if (version is >= 6 and < 56)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldFreeSkillPoints(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Points INTEGER NOT NULL, Revision INTEGER NOT NULL);
                    """
                );
            }
            if (version is >= 6 and < 57)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldLoyaltyWallet(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        IssuerCorporationId INTEGER NOT NULL, Points INTEGER NOT NULL,
                        PRIMARY KEY(PilotId,IssuerCorporationId));
                    CREATE TABLE IF NOT EXISTS WorldRedeemedCurrency(
                        TokenId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldRedeemToken(Id) ON DELETE CASCADE,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Kind INTEGER NOT NULL, Amount INTEGER NOT NULL, Before INTEGER NOT NULL,
                        After INTEGER NOT NULL, Time INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldRedeemedCurrency_PilotId_Time ON WorldRedeemedCurrency(PilotId,Time);
                    """
                );
            }
            if (version is >= 6 and < 58)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldImplantState(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Revision INTEGER NOT NULL);
                    CREATE TABLE IF NOT EXISTS WorldPilotImplant(
                        ItemId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        CloneId INTEGER NOT NULL REFERENCES WorldJumpClone(Id) ON DELETE CASCADE,
                        TypeId INTEGER NOT NULL, Slot INTEGER NOT NULL, Attribute INTEGER NOT NULL, Bonus INTEGER NOT NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldPilotImplant_CloneId_Slot ON WorldPilotImplant(CloneId,Slot);
                    """
                );
            }
            if (version is >= 6 and < 59)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldBooster(
                        ItemId INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        TypeId INTEGER NOT NULL, Slot INTEGER NOT NULL, Bonus INTEGER NOT NULL,
                        StartedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldBooster_PilotId_Slot ON WorldBooster(PilotId,Slot);
                    CREATE TABLE IF NOT EXISTS WorldBoosterState(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE, Revision INTEGER NOT NULL);
                    """
                );
                if (
                    world
                        .Database.SqlQueryRaw<long>(
                            "SELECT COUNT(*) AS Value FROM pragma_table_info('WorldSkillQueue') WHERE name='PeriodsJson'"
                        )
                        .AsEnumerable()
                        .Single() == 0
                )
                {
                    world.Database.ExecuteSqlRaw("ALTER TABLE WorldSkillQueue ADD COLUMN PeriodsJson TEXT NOT NULL DEFAULT '[]'");
                }
            }
            if (version is >= 6 and < 60)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldSkillInjectorState(
                        PilotId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        NextAlphaInjection INTEGER NOT NULL, Revision INTEGER NOT NULL);
                    CREATE TABLE IF NOT EXISTS WorldSkillInjection(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ItemId INTEGER NOT NULL, TypeId INTEGER NOT NULL, Quantity INTEGER NOT NULL, Points INTEGER NOT NULL,
                        TotalBefore INTEGER NOT NULL, TotalAfter INTEGER NOT NULL, UsesSpent INTEGER NOT NULL, Time INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldSkillInjection_PilotId_Time ON WorldSkillInjection(PilotId,Time);
                    """
                );
                if (
                    world
                        .Database.SqlQueryRaw<long>(
                            "SELECT COUNT(*) AS Value FROM pragma_table_info('WorldBooster') WHERE name='RemainingInjectionUses'"
                        )
                        .AsEnumerable()
                        .Single() == 0
                )
                {
                    world.Database.ExecuteSqlRaw("ALTER TABLE WorldBooster ADD COLUMN RemainingInjectionUses INTEGER NOT NULL DEFAULT 0");
                }
            }
            if (version is >= 6 and < 61)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldSkillExtraction(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ItemId INTEGER NOT NULL, Time INTEGER NOT NULL, AllocationsJson TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldSkillExtraction_PilotId_Id ON WorldSkillExtraction(PilotId,Id);
                    """
                );
            }
            if (version is >= 6 and < 62)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldMail(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        SenderId INTEGER NOT NULL,
                        SentAt INTEGER NOT NULL, Subject TEXT NOT NULL, Body TEXT NOT NULL, RecipientsJson TEXT NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldMail_SenderId_SentAt ON WorldMail(SenderId,SentAt);
                    CREATE TABLE IF NOT EXISTS WorldMailEntry(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        MessageId INTEGER NOT NULL REFERENCES WorldMail(Id) ON DELETE CASCADE,
                        Status INTEGER NOT NULL, Labels INTEGER NOT NULL, Deleted INTEGER NOT NULL,
                        PRIMARY KEY(PilotId,MessageId));
                    CREATE INDEX IF NOT EXISTS IX_WorldMailEntry_MessageId ON WorldMailEntry(MessageId);
                    CREATE TABLE IF NOT EXISTS WorldMailLabel(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Id INTEGER NOT NULL, Name TEXT NOT NULL, Color INTEGER, PRIMARY KEY(PilotId,Id));
                    CREATE TABLE IF NOT EXISTS WorldMailChange(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        MessageId INTEGER NOT NULL, Origin TEXT NOT NULL, Kind INTEGER NOT NULL,
                        Status INTEGER NOT NULL, Labels INTEGER NOT NULL, LabelId INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldMailChange_PilotId_Id ON WorldMailChange(PilotId,Id);
                    """
                );
            }
            if (version is >= 6 and < 63)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldMailingList(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL,
                        Key TEXT NOT NULL, Name TEXT NOT NULL, DefaultAccess INTEGER NOT NULL, DefaultRole INTEGER NOT NULL,
                        Deleted INTEGER NOT NULL, WelcomeTitle TEXT, WelcomeBody TEXT);
                    CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldMailingList_Key ON WorldMailingList(Key) WHERE Deleted = 0;
                    CREATE TABLE IF NOT EXISTS WorldMailingListMember(
                        ListId INTEGER NOT NULL REFERENCES WorldMailingList(Id) ON DELETE CASCADE,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        Role INTEGER NOT NULL, PRIMARY KEY(ListId,PilotId));
                    CREATE INDEX IF NOT EXISTS IX_WorldMailingListMember_PilotId ON WorldMailingListMember(PilotId);
                    CREATE TABLE IF NOT EXISTS WorldMailingListAccess(
                        ListId INTEGER NOT NULL REFERENCES WorldMailingList(Id) ON DELETE CASCADE,
                        EntityId INTEGER NOT NULL, Access INTEGER NOT NULL, PRIMARY KEY(ListId,EntityId));
                    CREATE TABLE IF NOT EXISTS WorldMailingListChange(
                        Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        ListId INTEGER NOT NULL, Origin TEXT NOT NULL, Kind INTEGER NOT NULL, Role INTEGER NOT NULL);
                    CREATE INDEX IF NOT EXISTS IX_WorldMailingListChange_PilotId_Id ON WorldMailingListChange(PilotId,Id);
                    """
                );
                if (
                    world
                        .Database.SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('WorldMail') WHERE name='ListId'")
                        .AsEnumerable()
                        .Single() == 0
                )
                {
                    world.Database.ExecuteSqlRaw("ALTER TABLE WorldMail ADD COLUMN ListId INTEGER");
                }
                world.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_WorldMail_ListId ON WorldMail(ListId)");
            }
            if (version is >= 6 and < 64)
            {
                world.Database.ExecuteSqlRaw(
                    """
                    CREATE TABLE IF NOT EXISTS WorldPilotNote(
                        PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                        TargetId INTEGER NOT NULL, Text TEXT NOT NULL, PRIMARY KEY(PilotId,TargetId));
                    """
                );
            }
            NpcPopulationSchema.Upgrade(world);
            NpcMissileSchema.Upgrade(world);
            NpcEncounterSchema.Upgrade(world);
            NpcGuardSchema.Upgrade(world);
            world.Database.ExecuteSqlRaw("PRAGMA user_version=68");
        }
        world.Database.ExecuteSqlRaw(
            """
            INSERT OR IGNORE INTO WorldHomeStation(PilotId,StationId)
                SELECT p.Id,s.StationId FROM WorldPilot p JOIN WorldShip s ON s.Id=p.ActiveShipId;
            """
        );
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
        CorporationSchema.Upgrade(world);
        foreach (int corporation in world.Pilots.Select(p => p.CorporationId).Distinct())
        {
            if (
                !catalog.Set<NpcCorporation>().Any(c => c.Id == corporation) && !world.Set<SavedCorporation>().Any(c => c.Id == corporation)
            )
            {
                throw new InvalidDataException($"Save references unknown pilot corporation {corporation}.");
            }
        }
        ShipStoragePersistence.Check(world);
        if (version < 69)
        {
            ResidentFitSchema.Create(world);
            world.Database.ExecuteSqlRaw("PRAGMA user_version=69");
        }
        if (version < 70)
        {
            world.Database.ExecuteSqlRaw(
                """
                CREATE TABLE IF NOT EXISTS WorldWeaponLink(
                    SlaveId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    MasterId INTEGER NOT NULL REFERENCES WorldItem(Id) ON DELETE CASCADE,
                    ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                    CHECK(SlaveId<>MasterId));
                CREATE INDEX IF NOT EXISTS IX_WorldWeaponLink_MasterId ON WorldWeaponLink(MasterId);
                CREATE INDEX IF NOT EXISTS IX_WorldWeaponLink_ShipId ON WorldWeaponLink(ShipId);
                PRAGMA user_version=70;
                """
            );
        }
        if (version < 71)
        {
            InteractionHistorySchema.Upgrade(world);
            world.Database.ExecuteSqlRaw("PRAGMA user_version=71");
        }
        world.Database.ExecuteSqlRaw("PRAGMA user_version=77");
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
