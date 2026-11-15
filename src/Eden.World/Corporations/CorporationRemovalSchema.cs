using Eden.Data.World.Characters;
using Eden.Data.World.Corporations;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Corporations;

internal static class CorporationRemovalSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCorporationRemoval>().ToTable("WorldCorporationRemoval").HasKey(row => row.PilotId);
        model.Entity<SavedCorporationRemoval>().HasIndex(row => row.CorporationId);
        model
            .Entity<SavedCorporationRemoval>()
            .HasOne<Pilot>()
            .WithMany()
            .HasForeignKey(row => row.PilotId)
            .OnDelete(DeleteBehavior.NoAction);
        model
            .Entity<SavedCorporationRemoval>()
            .HasOne<SavedCorporation>()
            .WithMany()
            .HasForeignKey(row => row.CorporationId)
            .OnDelete(DeleteBehavior.NoAction);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCorporationRemoval(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id),
                CorporationId INTEGER NOT NULL REFERENCES WorldCorporation(Id), ActorId INTEGER NOT NULL,
                MembershipId INTEGER NOT NULL, DestinationCorporationId INTEGER NOT NULL, RequestedAt INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldCorporationRemoval_CorporationId ON WorldCorporationRemoval(CorporationId);
            """
        );
}
