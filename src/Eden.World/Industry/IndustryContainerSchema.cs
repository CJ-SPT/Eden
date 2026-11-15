using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Industry;

internal static class IndustryContainerSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldIndustryJob')").ToHashSet();
        if (!columns.Contains("OutputLocationId"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIndustryJob ADD COLUMN OutputLocationId INTEGER");
        }
        if (!columns.Contains("OutputFlagId"))
        {
            // Build 3662168 flagHangar = 4; pre-container jobs always delivered to personal Item Hangar.
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldIndustryJob ADD COLUMN OutputFlagId INTEGER NOT NULL DEFAULT 4");
        }
    }
}
