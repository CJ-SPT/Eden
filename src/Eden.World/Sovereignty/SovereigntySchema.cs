using Eden.Data.World.Alliances;
using Eden.Data.World.Corporations;
using Eden.Data.World.Sovereignty;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Sovereignty;

internal static class SovereigntySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedSovereigntyClaim>().ToTable("WorldSovereigntyClaim").HasKey(row => row.SystemId);
        model.Entity<SavedSovereigntyClaim>().Property(row => row.SystemId).ValueGeneratedNever();
        model.Entity<SavedSovereigntyClaim>().HasIndex(row => row.HubId).IsUnique();
        model.Entity<SavedSovereigntyClaim>().HasIndex(row => row.AllianceId);
        model.Entity<SavedSovereigntyClaim>().HasOne<SavedAlliance>().WithMany().HasForeignKey(row => row.AllianceId);
        model.Entity<SavedSovereigntyClaim>().HasOne<SavedCorporation>().WithMany().HasForeignKey(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldSovereigntyClaim(
                SystemId INTEGER PRIMARY KEY, HubId INTEGER NOT NULL,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id),
                AllianceId INTEGER NOT NULL REFERENCES WorldAlliance(Id), ClaimedAt INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldSovereigntyClaim_HubId ON WorldSovereigntyClaim(HubId);
            CREATE INDEX IF NOT EXISTS IX_WorldSovereigntyClaim_AllianceId ON WorldSovereigntyClaim(AllianceId);
            """
        );
}
