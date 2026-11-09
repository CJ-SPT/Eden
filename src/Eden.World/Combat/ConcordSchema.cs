using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class ConcordSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedConcordResponse>().ToTable("WorldConcordResponse").HasKey(r => r.ShipId);
        model.Entity<SavedConcordResponse>().Property(r => r.ShipId).ValueGeneratedNever();
        model.Entity<SavedConcordResponse>().HasIndex(r => new { r.PilotId, r.CompletedAt });
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldConcordResponse(
                ShipId INTEGER PRIMARY KEY, PilotId INTEGER NOT NULL, SystemId INTEGER NOT NULL,
                RespondAt INTEGER NOT NULL, DestroyAt INTEGER NOT NULL, CommanderId INTEGER,
                FirstCaptainId INTEGER, SecondCaptainId INTEGER, CompletedAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldConcordResponse_PilotId_CompletedAt ON WorldConcordResponse(PilotId,CompletedAt);
            """
        );
}
