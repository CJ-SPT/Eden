using Eden.Data.World.Characters;
using Eden.Gameplay.Characters;
using Eden.Infrastructure.Registration;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

[Injectable]
internal sealed class PilotActivityStore(WorldDatabase database, TimeProvider clock) : IPilotActivity
{
    public void Record(long pilotId, bool online)
    {
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        if (world.Pilots.Find(pilotId) is null)
        {
            return;
        }
        var row = world.Set<SavedPilotActivity>().Find(pilotId) ?? world.Add(new SavedPilotActivity { PilotId = pilotId }).Entity;
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (online)
        {
            row.LoggedOnAt = now;
        }
        else
        {
            row.LoggedOffAt = now;
        }
        transaction.Commit();
    }
}

internal static class PilotActivitySchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedPilotActivity>().ToTable("WorldPilotActivity").HasKey(row => row.PilotId);
        model.Entity<SavedPilotActivity>().HasOne<Pilot>().WithOne().HasForeignKey<SavedPilotActivity>(row => row.PilotId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldPilotActivity(
                PilotId INTEGER PRIMARY KEY REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                LoggedOnAt INTEGER, LoggedOffAt INTEGER);
            """
        );
}
