using Eden.Data.World.Alliances;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Alliances;

internal static class AllianceBillingSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedAllianceBill>().ToTable("WorldAllianceBill").HasKey(row => row.Id);
        model.Entity<SavedAllianceBill>().HasIndex(row => row.AllianceId);
        model.Entity<SavedAllianceBill>().HasIndex(row => new { row.AllianceId, row.DueAt }).IsUnique();
        model.Entity<SavedAllianceLifecycle>().ToTable("WorldAllianceLifecycle").HasKey(row => row.AllianceId);
        model.Entity<SavedAllianceLifecycle>().Property(row => row.AllianceId).ValueGeneratedNever();
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAllianceBill(
                Id INTEGER PRIMARY KEY AUTOINCREMENT, AllianceId INTEGER NOT NULL, IssuedAt INTEGER NOT NULL,
                DueAt INTEGER NOT NULL, AmountCents INTEGER NOT NULL, CorporationCount INTEGER NOT NULL,
                PaidAt INTEGER, PaidByCorporationId INTEGER, DivisionKey INTEGER NOT NULL, Renewed INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldAllianceBill_AllianceId ON WorldAllianceBill(AllianceId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldAllianceBill_AllianceId_DueAt ON WorldAllianceBill(AllianceId,DueAt);
            CREATE TABLE IF NOT EXISTS WorldAllianceLifecycle(AllianceId INTEGER PRIMARY KEY, ClosureAt INTEGER);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldInboxNotification')")
                .AsEnumerable()
                .Contains("AllianceId")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldInboxNotification ADD COLUMN AllianceId INTEGER");
        }
    }
}
