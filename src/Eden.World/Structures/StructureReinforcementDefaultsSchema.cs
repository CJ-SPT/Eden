using Eden.Data.World.Corporations;
using Eden.Data.World.Structures;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Structures;

internal static class StructureReinforcementDefaultsSchema
{
    internal const int Version = 240;

    internal static void Configure(ModelBuilder model)
    {
        var settings = model.Entity<SavedStructureReinforcementDefault>();
        settings.ToTable(
            "WorldStructureReinforcementDefault",
            table => table.HasCheckConstraint("CK_StructureReinforcementDefault_Hour", "Hour BETWEEN 0 AND 23")
        );
        settings.HasKey(row => row.CorporationId);
        settings.Property(row => row.CorporationId).ValueGeneratedNever();
        settings.HasOne<SavedCorporation>().WithMany().HasForeignKey(row => row.CorporationId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldStructureReinforcementDefault(
                CorporationId INTEGER PRIMARY KEY REFERENCES WorldCorporation(Id) ON DELETE CASCADE,
                Hour INTEGER NOT NULL CONSTRAINT CK_StructureReinforcementDefault_Hour CHECK(Hour BETWEEN 0 AND 23));
            """
        );
}
