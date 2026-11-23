using Eden.Data.World.Characters;
using Eden.Data.World.Docking;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Docking;

internal static class ShipSkinLicenseSchema
{
    internal static void Configure(ModelBuilder model)
    {
        var row = model.Entity<SavedShipSkinLicense>();
        row.ToTable("WorldShipSkinLicense").HasKey(license => new { license.PilotId, license.SkinId });
        row.HasOne<Pilot>().WithMany().HasForeignKey(license => license.PilotId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldShipSkinLicense(
                PilotId INTEGER NOT NULL REFERENCES WorldPilot(Id) ON DELETE CASCADE,
                SkinId INTEGER NOT NULL,
                Expires INTEGER,
                IsSingleUse INTEGER NOT NULL,
                PRIMARY KEY(PilotId,SkinId));
            """
        );
}
