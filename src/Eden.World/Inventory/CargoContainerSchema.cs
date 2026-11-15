using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static class CargoContainerSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedCargoContainer>().ToTable("WorldCargoContainer").HasKey(row => row.ItemId);
        model.Entity<SavedCargoContainer>().HasOne<OwnedItem>().WithMany().HasForeignKey(row => row.ItemId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldCargoContainer(
                ItemId INTEGER NOT NULL PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                Name TEXT,GeneralPassword TEXT,ConfigurationPassword TEXT,Configuration INTEGER NOT NULL DEFAULT 0)
            """
        );
}
