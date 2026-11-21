using Eden.Data.World.Calendar;
using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationCalendarSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var rows = model.Entity<SavedCorporationCalendarEvent>();
        rows.ToTable("WorldCorporationCalendarEvent").HasKey(e => e.Id);
        rows.Property(e => e.Id).ValueGeneratedNever();
        rows.HasOne<SavedCorporation>().WithMany().HasForeignKey(e => e.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationCalendarEvent(
                Id INTEGER PRIMARY KEY, CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                DataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationCalendarEvent_CorporationId ON WorldCorporationCalendarEvent(CorporationId);
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldCorporationChange')")
                .Contains("CalendarJson")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldCorporationChange ADD COLUMN CalendarJson TEXT");
        }
    }
}
