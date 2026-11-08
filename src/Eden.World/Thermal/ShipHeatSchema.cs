using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal static class ShipHeatSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedShipHeat>().ToTable("WorldShipHeat").HasKey(h => h.ShipId);
        model.Entity<SavedShipHeat>().HasOne<Ship>().WithMany().HasForeignKey(h => h.ShipId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldShipHeat(
                ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                StateJson TEXT NOT NULL);
            """
        );
}
