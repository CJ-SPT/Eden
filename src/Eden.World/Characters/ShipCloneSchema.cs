using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

internal static class ShipCloneSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldJumpClone')").ToArray();
        if (columns.Contains("StationId"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldJumpClone RENAME COLUMN StationId TO LocationId");
        }
        if (!columns.Contains("InShip"))
        {
            world.Database.ExecuteSqlRaw(
                "ALTER TABLE WorldJumpClone ADD COLUMN InShip INTEGER NOT NULL DEFAULT 0 CHECK (Active = 0 OR InShip = 0)"
            );
        }
        world.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_WorldJumpClone_LocationId ON WorldJumpClone(LocationId) WHERE InShip = 1"
        );
    }
}
