using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static class InventoryLookupSchema
{
    internal static void Upgrade(WorldDbContext world) =>
        world.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS IX_WorldItem_LocationId ON WorldItem(LocationId)");
}
