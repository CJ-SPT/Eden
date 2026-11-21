using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static class ShipSharingSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedShipSharing>().ToTable("WorldShipSharing").HasKey(s => s.ShipId);
        model.Entity<SavedShipSharing>().HasOne<Ship>().WithMany().HasForeignKey(s => s.ShipId).OnDelete(DeleteBehavior.Cascade);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldShipSharing(
                ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                FleetHangarFleet INTEGER NOT NULL DEFAULT 0,
                FleetHangarCorporation INTEGER NOT NULL DEFAULT 0);
            """
        );
}
