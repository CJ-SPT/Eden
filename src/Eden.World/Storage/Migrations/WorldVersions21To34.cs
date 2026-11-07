using Eden.Gameplay.Combat;
using Eden.World.Combat;
using Eden.World.Combat.Npcs;
using Eden.World.Loot;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersions21To34
{
    internal static void Apply(WorldDbContext world, TimeProvider clock, int version)
    {
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
    }
}
