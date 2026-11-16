using Eden.Data.World.Characters;
using Eden.Data.World.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class WormholeTravelSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var connection = model.Entity<SavedWormholeConnection>();
        connection.ToTable("WorldWormholeConnection").HasKey(c => c.Id);
        connection.Property(c => c.Id).ValueGeneratedNever();
        connection.HasIndex(c => c.EndpointId).IsUnique();
        connection.HasIndex(c => c.SourceSystem);
        connection.HasIndex(c => c.DestinationSystem);
        var jump = model.Entity<SavedWormholeJump>();
        jump.ToTable("WorldWormholeJump").HasKey(j => new { j.PilotId, j.EntranceId });
        jump.HasOne<Pilot>().WithMany().HasForeignKey(j => j.PilotId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldWormholeConnection (
                Id INTEGER PRIMARY KEY,
                EndpointId INTEGER NOT NULL,
                SourceSystem INTEGER NOT NULL,
                DestinationSystem INTEGER NOT NULL,
                TypeId INTEGER NOT NULL,
                Generation INTEGER NOT NULL,
                ActivatedAt INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL,
                ExitVisible INTEGER NOT NULL,
                TotalMass REAL NOT NULL,
                RemainingMass REAL NOT NULL,
                MassUpdatedAt INTEGER NOT NULL,
                ReplaceAt INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldWormholeConnection_EndpointId ON WorldWormholeConnection(EndpointId);
            CREATE INDEX IF NOT EXISTS IX_WorldWormholeConnection_SourceSystem ON WorldWormholeConnection(SourceSystem);
            CREATE INDEX IF NOT EXISTS IX_WorldWormholeConnection_DestinationSystem ON WorldWormholeConnection(DestinationSystem);
            CREATE TABLE IF NOT EXISTS WorldWormholeJump (
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                EntranceId INTEGER NOT NULL,
                JumpedAt INTEGER NOT NULL,
                PRIMARY KEY(PilotId,EntranceId)
            );
            """
        );
}
