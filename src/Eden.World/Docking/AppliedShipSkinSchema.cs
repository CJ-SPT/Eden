using Eden.Data.World.Docking;
using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

internal static class AppliedShipSkinSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<SavedAppliedShipSkin>();
        row.ToTable("WorldAppliedShipSkin").HasKey(skin => skin.ShipId);
        row.HasOne<Ship>().WithOne().HasForeignKey<SavedAppliedShipSkin>(skin => skin.ShipId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldAppliedShipSkin(
                ShipId INTEGER PRIMARY KEY REFERENCES WorldShip(Id) ON DELETE CASCADE,
                PilotId INTEGER NOT NULL,
                SkinId INTEGER NOT NULL,
                Expires INTEGER);
            """
        );
}
