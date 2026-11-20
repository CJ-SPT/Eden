using Eden.Data.World.FactionWarfare;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.FactionWarfare;

internal static class FactionWarzoneSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedWarzoneOccupation>().ToTable("WorldWarzoneOccupation").HasKey(row => row.SystemId);
        model.Entity<SavedWarzoneOccupation>().Property(row => row.SystemId).ValueGeneratedNever();
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldWarzoneOccupation(
                SystemId INTEGER PRIMARY KEY,
                Occupier INTEGER NOT NULL,
                VictoryPoints INTEGER NOT NULL,
                CapturedAt INTEGER);
            """
        );
}
