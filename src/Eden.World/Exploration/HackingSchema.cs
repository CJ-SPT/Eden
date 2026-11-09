using Eden.Data.World.Exploration;
using Eden.Gameplay.Exploration;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Exploration;

internal static class HackingSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedExplorationSite>().ToTable("WorldExplorationSite").HasKey(s => s.Id);
        model.Entity<SavedExplorationSite>().HasIndex(s => new { s.SystemId, s.Kind });
        model.Entity<SavedHackingContainer>().ToTable("WorldHackingContainer").HasKey(c => c.Id);
        model.Entity<SavedHackingContainer>().HasIndex(c => c.SiteId);
        model.Entity<SavedHackingAttempt>().ToTable("WorldHackingAttempt").HasKey(a => a.PilotId);
        model.Entity<SavedHackingAttempt>().HasIndex(a => a.ContainerId).IsUnique();
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldExplorationSite (
                Id INTEGER PRIMARY KEY, SystemId INTEGER NOT NULL, Kind INTEGER NOT NULL, DungeonId INTEGER NOT NULL,
                X REAL NOT NULL, Y REAL NOT NULL, Z REAL NOT NULL, ExpiresAt INTEGER NOT NULL, ReplaceAt INTEGER);
            CREATE INDEX IF NOT EXISTS IX_WorldExplorationSite_SystemId_Kind ON WorldExplorationSite(SystemId,Kind);
            CREATE TABLE IF NOT EXISTS WorldHackingContainer (
                Id INTEGER PRIMARY KEY, SiteId INTEGER NOT NULL, "Index" INTEGER NOT NULL, Failures INTEGER NOT NULL,
                Hacked INTEGER NOT NULL, Destroyed INTEGER NOT NULL, Empty INTEGER NOT NULL,
            OwnerId INTEGER NOT NULL, LootSeed INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WorldHackingContainer_SiteId ON WorldHackingContainer(SiteId);
            CREATE TABLE IF NOT EXISTS WorldHackingAttempt (
            PilotId INTEGER PRIMARY KEY, ContainerId INTEGER NOT NULL, ContactJson TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldHackingAttempt_ContainerId ON WorldHackingAttempt(ContainerId);
            """
        );

    internal static void Reconcile(WorldDbContext world)
    {
        // Restart abandons the live board once. Paid capacitor and cooldowns remain spent.
        foreach (var attempt in world.Set<SavedHackingAttempt>().ToArray())
        {
            if (world.Set<SavedHackingContainer>().Find(attempt.ContainerId) is { Hacked: false, Destroyed: false } container)
            {
                container.Failures++;
                container.Destroyed = container.Failures >= HackingRules.MaximumFailures;
            }
            world.Remove(attempt);
        }
        world.SaveChanges();
    }
}
