using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationContactSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCorporationContact>().ToTable("WorldCorporationContact").HasKey(c => new { c.CorporationId, c.ContactId });
        model.Entity<SavedCorporationContact>().HasOne<SavedCorporation>().WithMany().HasForeignKey(c => c.CorporationId);
        model.Entity<SavedCorporationContactLabel>().ToTable("WorldCorporationContactLabel").HasKey(l => new { l.CorporationId, l.Id });
        model.Entity<SavedCorporationContactLabel>().HasOne<SavedCorporation>().WithMany().HasForeignKey(l => l.CorporationId);
        model.Entity<SavedCorporationContactLabel>().HasIndex(l => new { l.CorporationId, l.Name }).IsUnique();
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationContact(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                ContactId INTEGER NOT NULL, Standing REAL NOT NULL, LabelMask INTEGER NOT NULL,
                PRIMARY KEY(CorporationId,ContactId));
            CREATE TABLE IF NOT EXISTS WorldCorporationContactLabel(
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                Id INTEGER NOT NULL, Name TEXT NOT NULL, Color INTEGER, PRIMARY KEY(CorporationId,Id));
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldCorporationContactLabel_CorporationId_Name
                ON WorldCorporationContactLabel(CorporationId,Name);
            """
        );
}
