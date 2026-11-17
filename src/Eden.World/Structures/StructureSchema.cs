using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Structures;

internal static class StructureSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedStructure>().ToTable("WorldStructure").HasKey(s => s.Id);
        model.Entity<SavedStructure>().HasIndex(s => s.SystemId);
        model.Entity<SavedStructure>().HasIndex(s => s.CorporationId);
        model.Entity<SavedStructureBattle>().ToTable("WorldStructureBattle").HasKey(s => s.StructureId);
        model.Entity<SavedStructureBattle>().HasOne<SavedStructure>().WithMany().HasForeignKey(s => s.StructureId);
        model.Entity<SavedAssetSafety>().ToTable("WorldAssetSafety").HasKey(s => s.Id);
        model.Entity<SavedAssetSafety>().HasIndex(s => s.OwnerId);
        model.Entity<SavedAssetSafety>().HasIndex(s => s.SystemId);
        model.Entity<SavedStructureLoot>().ToTable("WorldStructureLoot").HasKey(s => s.Id);
        model.Entity<SavedStructureLoot>().HasIndex(s => s.SystemId);
        model.Entity<SavedStructureLoot>().HasIndex(s => s.OwnerId);
        model.Entity<SavedMoonExtraction>().ToTable("WorldMoonExtraction").HasKey(e => e.Id);
        model.Entity<SavedMoonExtraction>().HasIndex(e => new { e.StructureId, e.Ended }).IsUnique().HasFilter("Ended = 0");
        model.Entity<SavedMoonExtraction>().HasIndex(e => new { e.SystemId, e.AutoFractureAt });
        model.Entity<SavedMoonExtraction>().HasIndex(e => new { e.SystemId, e.FieldExpiresAt });
        model.Entity<SavedStructureService>().ToTable("WorldStructureService").HasKey(s => s.ItemId);
        model.Entity<SavedStructureService>().HasOne<SavedStructure>().WithMany().HasForeignKey(s => s.StructureId);
        model.Entity<SavedStructureService>().HasOne<OwnedItem>().WithMany().HasForeignKey(s => s.ItemId);
        model.Entity<SavedStructureService>().HasIndex(s => new { s.StructureId, s.Slot }).IsUnique();
        model.Entity<SavedStructureAccess>().ToTable("WorldStructureAccess").HasKey(s => new { s.StructureId, s.MemberId });
        model.Entity<SavedStructureAccess>().HasOne<SavedStructure>().WithMany().HasForeignKey(s => s.StructureId);
        model.Entity<Ship>().Ignore(s => s.DockedLocationId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldStructureBattle (
                StructureId INTEGER PRIMARY KEY REFERENCES WorldStructure(Id) ON DELETE CASCADE,
                Shield REAL NOT NULL, Armor REAL NOT NULL, Hull REAL NOT NULL, RepairAt INTEGER,
                DamageWindowAt INTEGER NOT NULL, ShieldWindow REAL NOT NULL, ArmorWindow REAL NOT NULL,
                HullWindow REAL NOT NULL, PausedUntil INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS WorldAssetSafety (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, OwnerId INTEGER NOT NULL, StructureId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL, Name TEXT NOT NULL, CreatedAt INTEGER NOT NULL, NearestStationId INTEGER NOT NULL,
                DestinationId INTEGER, DeliveredAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldAssetSafety_OwnerId ON WorldAssetSafety(OwnerId);
            CREATE INDEX IF NOT EXISTS IX_WorldAssetSafety_SystemId ON WorldAssetSafety(SystemId);
            CREATE TABLE IF NOT EXISTS WorldStructureLoot (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, StructureId INTEGER NOT NULL, OwnerId INTEGER NOT NULL,
                CorporationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, Name TEXT NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, ExpiresAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldStructureLoot_SystemId ON WorldStructureLoot(SystemId);
            CREATE INDEX IF NOT EXISTS IX_WorldStructureLoot_OwnerId ON WorldStructureLoot(OwnerId);
            """
        );
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldIndustryJob')").Contains("SuspendedAt"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIndustryJob ADD COLUMN SuspendedAt INTEGER");
        }
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldShip')").Contains("StructureId"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldShip ADD COLUMN StructureId INTEGER");
        }
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldStructure (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, TypeId INTEGER NOT NULL, Name TEXT NOT NULL, Description TEXT NOT NULL,
                CorporationId INTEGER NOT NULL, SystemId INTEGER NOT NULL, MoonId INTEGER,
                ReinforceWeekday INTEGER NOT NULL, ReinforceHour INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, Yaw REAL NOT NULL, State INTEGER NOT NULL,
                StateEndsAt INTEGER, StateStartedAt INTEGER NOT NULL, UnanchorAt INTEGER,
                HasCore INTEGER NOT NULL, PublicAccess INTEGER NOT NULL,
                LowPowerAt INTEGER, DeployedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldStructure_SystemId ON WorldStructure(SystemId);
            CREATE INDEX IF NOT EXISTS IX_WorldStructure_CorporationId ON WorldStructure(CorporationId);
            CREATE TABLE IF NOT EXISTS WorldMoonExtraction (
                Id INTEGER PRIMARY KEY AUTOINCREMENT, StructureId INTEGER NOT NULL, MoonId INTEGER NOT NULL,
                SystemId INTEGER NOT NULL, CorporationId INTEGER NOT NULL, StartedAt INTEGER NOT NULL,
                ReadyAt INTEGER NOT NULL, AutoFractureAt INTEGER NOT NULL, FracturedAt INTEGER, FieldExpiresAt INTEGER,
                Ended INTEGER NOT NULL, Composition TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldMoonExtraction_StructureId_Ended
                ON WorldMoonExtraction(StructureId,Ended) WHERE Ended = 0;
            CREATE INDEX IF NOT EXISTS IX_WorldMoonExtraction_SystemId_AutoFractureAt
                ON WorldMoonExtraction(SystemId,AutoFractureAt);
            CREATE INDEX IF NOT EXISTS IX_WorldMoonExtraction_SystemId_FieldExpiresAt
                ON WorldMoonExtraction(SystemId,FieldExpiresAt);
            CREATE TABLE IF NOT EXISTS WorldStructureService (
                ItemId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                StructureId INTEGER NOT NULL REFERENCES WorldStructure(Id) ON DELETE CASCADE,
                Slot INTEGER NOT NULL, Online INTEGER NOT NULL, NextFuelAt INTEGER);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldStructureService_StructureId_Slot ON WorldStructureService(StructureId,Slot);
            CREATE TABLE IF NOT EXISTS WorldStructureAccess (
                StructureId INTEGER NOT NULL REFERENCES WorldStructure(Id) ON DELETE CASCADE,
                MemberId INTEGER NOT NULL, Allowed INTEGER NOT NULL, PRIMARY KEY(StructureId,MemberId));
            """
        );
    }
}
