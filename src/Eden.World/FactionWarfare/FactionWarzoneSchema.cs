using Eden.Data.World.Characters;
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
        model.Entity<SavedMilitiaEnlistment>().ToTable("WorldMilitiaEnlistment").HasKey(row => row.PilotId);
        model.Entity<SavedMilitiaEnlistment>().Property(row => row.PilotId).ValueGeneratedNever();
        model.Entity<SavedMilitiaEnlistment>().HasOne<Pilot>().WithMany().HasForeignKey(row => row.PilotId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldWarzoneOccupation(
                SystemId INTEGER PRIMARY KEY,
                Occupier INTEGER NOT NULL,
                VictoryPoints INTEGER NOT NULL,
                CapturedAt INTEGER);
            CREATE TABLE IF NOT EXISTS WorldMilitiaEnlistment(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                DirectFaction INTEGER,
                DirectCooldownUntil INTEGER NOT NULL);
            """
        );
}
