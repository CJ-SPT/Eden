using Eden.Data.World.Characters;
using Eden.Data.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Skills;

internal static class ExpertSystemSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<SavedExpertSystem>();
        row.ToTable("WorldExpertSystem").HasKey(system => new { system.PilotId, system.TypeId });
        row.HasOne<Pilot>().WithMany().HasForeignKey(system => system.PilotId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldExpertSystem(
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                TypeId INTEGER NOT NULL,
                InstalledAt INTEGER NOT NULL,
                ExpiresAt INTEGER NOT NULL,
                PRIMARY KEY(PilotId,TypeId));
            """
        );
}
