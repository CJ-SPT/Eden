using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationRecruitmentSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCorporationRecruitmentAd>().ToTable("WorldCorporationRecruitmentAd").HasKey(ad => ad.Id);
        model.Entity<SavedCorporationRecruitmentAd>().HasOne<SavedCorporation>().WithMany().HasForeignKey(ad => ad.CorporationId);
        model.Entity<SavedCorporationRecruitmentAd>().HasIndex(ad => ad.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationRecruitmentAd(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                CreatedAt INTEGER NOT NULL, ExpiresAt INTEGER NOT NULL, DetailsJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationRecruitmentAd_CorporationId
                ON WorldCorporationRecruitmentAd(CorporationId);
            """
        );
}
