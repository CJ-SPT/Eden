using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Characters;

internal static class HomeStationSchema
{
    internal const int RemoteOfficeVersion = 235;

    internal static void UpgradeRemoteOffice(WorldDbContext world)
    {
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldHomeStation')")
                .Contains("NextRemoteChange")
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldHomeStation ADD COLUMN NextRemoteChange INTEGER");
        }
    }
}
