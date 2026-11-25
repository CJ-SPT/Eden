using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class WarReportSchema
{
    internal const int Version = 228;
    internal const string Table = "WorldCorporationWarKill";

    internal static void Configure(ModelBuilder model)
    {
        var kills = model.Entity<SavedCorporationWarKill>();
        kills.ToTable(Table).HasKey(row => row.Id);
        kills.Property(row => row.Id).ValueGeneratedNever();
        kills.HasIndex(row => new { row.WarId, row.Time });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationWarKill(
                Id INTEGER PRIMARY KEY, WarId INTEGER NOT NULL, VictimOrganizationId INTEGER NOT NULL, AttackerLoss INTEGER NOT NULL,
                GroupId INTEGER NOT NULL, "Group" INTEGER, Time INTEGER NOT NULL, IskLost TEXT NOT NULL, IskDestroyed TEXT NOT NULL,
                Report TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationWarKill_WarId_Time ON WorldCorporationWarKill(WarId,Time);
            """
        );
}
