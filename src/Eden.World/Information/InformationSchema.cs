using Eden.Data.World.Information;
using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Information;

internal static class InformationSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedInformationCycle>().ToTable("WorldInformationCycle").HasKey(c => c.ModuleId);
        model.Entity<SavedInformationCycle>().HasOne<OwnedItem>().WithMany().HasForeignKey(c => c.ModuleId);
        model.Entity<SavedInformationCycle>().HasOne<Ship>().WithMany().HasForeignKey(c => c.ShipId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldInformationCycle(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL, ShipId INTEGER NOT NULL REFERENCES WorldShip(Id) ON DELETE CASCADE,
                TargetId INTEGER NOT NULL, Effect INTEGER NOT NULL, StartedAt INTEGER NOT NULL, ReadyAt INTEGER NOT NULL,
                ModuleJson TEXT NOT NULL);
            DELETE FROM WorldActiveModule WHERE ModuleId IN (SELECT ModuleId FROM WorldInformationCycle);
            DELETE FROM WorldInformationCycle;
            """
        );
}
