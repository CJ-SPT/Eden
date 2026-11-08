using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal static class NaniteRepairSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model
            .Entity<SavedNaniteRepair>()
            .ToTable(
                "WorldNaniteRepair",
                t =>
                {
                    t.HasCheckConstraint("CK_NaniteRepair_Clock", "StartedAt>=0 AND CompletesAt>StartedAt");
                    t.HasCheckConstraint("CK_NaniteRepair_Damage", "InitialDamage>0 AND InitialDamage<InitialHitPoints");
                    t.HasCheckConstraint("CK_NaniteRepair_HitPoints", "InitialHitPoints<=1.7976931348623157e308");
                    t.HasCheckConstraint("CK_NaniteRepair_Paste", "PasteUnits>=0");
                }
            )
            .HasKey(r => r.ModuleId);
        model.Entity<SavedNaniteRepair>().Property(r => r.Generation).UseCollation("NOCASE");
        model.Entity<SavedNaniteRepair>().HasIndex(r => r.Generation).IsUnique();
        model.Entity<SavedNaniteRepair>().HasOne<OwnedItem>().WithMany().HasForeignKey(r => r.ModuleId);
        model.Entity<SavedNaniteRepair>().HasOne<Pilot>().WithMany().HasForeignKey(r => r.PilotId);
        model.Entity<SavedNaniteRepair>().HasOne<Ship>().WithMany().HasForeignKey(r => r.ShipId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldNaniteRepair(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                Generation TEXT NOT NULL COLLATE NOCASE UNIQUE,
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                StartedAt INTEGER NOT NULL CHECK(StartedAt>=0),
                CompletesAt INTEGER NOT NULL CHECK(CompletesAt>StartedAt),
                InitialDamage REAL NOT NULL CHECK(InitialDamage>0 AND InitialDamage<InitialHitPoints),
                InitialHitPoints REAL NOT NULL CHECK(InitialHitPoints<=1.7976931348623157e308),
                PasteUnits INTEGER NOT NULL CHECK(PasteUnits>=0));
            CREATE INDEX IF NOT EXISTS IX_WorldNaniteRepair_ShipId ON WorldNaniteRepair(ShipId);
            """
        );
}
