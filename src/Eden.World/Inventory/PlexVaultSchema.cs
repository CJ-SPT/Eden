using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal static class PlexVaultSchema
{
    internal const int Version = 236;

    internal static void Upgrade(WorldDbContext world)
    {
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldAccount')").Contains("PlexBalance"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldAccount ADD COLUMN PlexBalance INTEGER NOT NULL DEFAULT 0");
        }
    }
}
