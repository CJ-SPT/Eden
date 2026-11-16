using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class SleeperSiteSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var site = model.Entity<SavedSleeperSite>();
        site.ToTable("WorldSleeperSite").HasKey(s => s.Id);
        site.Property(s => s.Id).ValueGeneratedNever();
        site.HasIndex(s => s.ParentSiteId);
        site.HasIndex(s => s.SystemId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSleeperSite (
                Id INTEGER PRIMARY KEY, ParentSiteId INTEGER, SystemId INTEGER NOT NULL,
                ActivatedAt INTEGER, ExpiresAt INTEGER, HackStarts INTEGER NOT NULL,
                HackFailures INTEGER NOT NULL, HackResolutions INTEGER NOT NULL, AggressedWave INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldSleeperSite_ParentSiteId ON WorldSleeperSite(ParentSiteId);
            CREATE INDEX IF NOT EXISTS IX_WorldSleeperSite_SystemId ON WorldSleeperSite(SystemId);
            """
        );
}
