using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Fittings;

internal static class TimedModuleSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldActiveModule')").Contains("ExpiresAt"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldActiveModule ADD COLUMN ExpiresAt INTEGER NOT NULL DEFAULT 0");
        }
        world.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_WorldActiveModule_ExpiresAt ON WorldActiveModule(ExpiresAt) WHERE ExpiresAt>0"
        );
    }
}
