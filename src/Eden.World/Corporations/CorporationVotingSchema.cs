using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationVotingSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCorporationVote>().ToTable("WorldCorporationVote").HasKey(row => row.Id);
        model.Entity<SavedCorporationVote>().HasOne<SavedCorporation>().WithMany().HasForeignKey(row => row.CorporationId);
        model.Entity<SavedCorporationVote>().HasIndex(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationVote(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                StartedAt INTEGER NOT NULL, EndsAt INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationVote_CorporationId ON WorldCorporationVote(CorporationId);
            """
        );
}
