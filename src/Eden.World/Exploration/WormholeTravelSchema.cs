using Eden.Data.World.Characters;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class WormholeTravelSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var jump = model.Entity<SavedWormholeJump>();
        jump.ToTable("WorldWormholeJump").HasKey(j => new { j.PilotId, j.EntranceId });
        jump.HasOne<Pilot>().WithMany().HasForeignKey(j => j.PilotId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldWormholeJump (
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                EntranceId INTEGER NOT NULL,
                JumpedAt INTEGER NOT NULL,
                PRIMARY KEY(PilotId,EntranceId)
            );
            """
        );
}
