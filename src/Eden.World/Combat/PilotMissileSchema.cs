using Eden.Data.World.Combat;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Combat;

internal static class PilotMissileSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        if (!world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMissile')").Contains("TargetKind"))
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldMissile ADD COLUMN TargetKind INTEGER NOT NULL DEFAULT 0;
                UPDATE WorldMissile SET TargetKind=1 WHERE SourceKind=1;
                """
            );
        }
    }
}
