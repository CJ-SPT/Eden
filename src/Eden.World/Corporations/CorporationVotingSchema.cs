using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
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
        model.Entity<SavedCorporationBlueprintLock>().ToTable("WorldCorporationBlueprintLock").HasKey(row => row.ItemId);
        model
            .Entity<SavedCorporationBlueprintLock>()
            .HasOne<OwnedItem>()
            .WithMany()
            .HasForeignKey(row => row.ItemId)
            .OnDelete(DeleteBehavior.NoAction);
        model
            .Entity<SavedCorporationBlueprintLock>()
            .HasOne<SavedCorporation>()
            .WithMany()
            .HasForeignKey(row => row.CorporationId)
            .OnDelete(DeleteBehavior.NoAction);
        model
            .Entity<SavedCorporationBlueprintLock>()
            .HasOne<SavedCorporationVote>()
            .WithMany()
            .HasForeignKey(row => row.VoteId)
            .OnDelete(DeleteBehavior.NoAction);
        model.Entity<SavedCorporationBlueprintLock>().HasIndex(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationVote(
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                StartedAt INTEGER NOT NULL, EndsAt INTEGER NOT NULL, DataJson TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationVote_CorporationId ON WorldCorporationVote(CorporationId);
            CREATE TABLE IF NOT EXISTS WorldCorporationBlueprintLock(
                ItemId INTEGER PRIMARY KEY REFERENCES WorldItem(Id),
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id),
                OfficeId INTEGER NOT NULL, StationId INTEGER NOT NULL, TypeId INTEGER NOT NULL, FlagId INTEGER NOT NULL,
                VoteId INTEGER NOT NULL REFERENCES WorldCorporationVote(Id), Permanent INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationBlueprintLock_CorporationId ON WorldCorporationBlueprintLock(CorporationId);
            """
        );
}
