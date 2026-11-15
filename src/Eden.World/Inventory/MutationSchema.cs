using Eden.Data.World.Inventory;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static class MutationSchema
{
    internal static void Configure(ModelBuilder model) =>
        model.Entity<SavedMutatedItem>().ToTable("WorldMutatedItem").HasKey(r => r.ItemId);

    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS WorldMutatedItem(ItemId INTEGER NOT NULL PRIMARY KEY,Payload TEXT NOT NULL)"
        );
}
