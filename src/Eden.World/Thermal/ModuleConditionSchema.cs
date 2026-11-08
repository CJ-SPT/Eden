using Eden.Data.World.Inventory;
using Eden.Data.World.Thermal;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Thermal;

internal static class ModuleConditionSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model
            .Entity<SavedModuleCondition>()
            .ToTable(
                "WorldModuleCondition",
                t => t.HasCheckConstraint("CK_ModuleCondition_Damage", "Damage>=0 AND Damage<=1.7976931348623157e308")
            )
            .HasKey(c => c.ModuleId);
        model.Entity<SavedModuleCondition>().HasOne<OwnedItem>().WithMany().HasForeignKey(c => c.ModuleId);
    }

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldModuleCondition(
                ModuleId INTEGER PRIMARY KEY REFERENCES WorldItem(Id) ON DELETE CASCADE,
                Damage REAL NOT NULL CHECK(Damage>=0 AND Damage<=1.7976931348623157e308));
            """
        );
}
