using Eden.Data.World.Capsuleers;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Capsuleers;

internal static class TrafficSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var identity = model.Entity<SavedTrafficIdentity>();
        identity.ToTable("WorldTrafficIdentity").HasKey(value => value.PilotId);
        // No cascading FK: a deleted pilot must not release a real character for reuse.
        identity.HasIndex(value => value.CharacterId).IsUnique();
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldTrafficIdentity(
                PilotId INTEGER PRIMARY KEY, CharacterId INTEGER NOT NULL UNIQUE,
                KillId INTEGER NOT NULL, SnapshotId TEXT NOT NULL);
            """
        );
    }
}
