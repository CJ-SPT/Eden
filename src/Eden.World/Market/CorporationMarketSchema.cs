using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal static class CorporationMarketSchema
{
    internal const int Version = 239;

    internal static void Upgrade(WorldDbContext world)
    {
        var columns = world.Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMarketTrade')").ToArray();
        if (!columns.Contains("CorporationId"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMarketTrade ADD COLUMN CorporationId INTEGER");
        }
        if (!columns.Contains("WalletDivisionKey"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMarketTrade ADD COLUMN WalletDivisionKey INTEGER");
        }
        world.Database.ExecuteSqlRaw(
            "CREATE INDEX IF NOT EXISTS IX_WorldMarketTrade_CorporationId_Time ON WorldMarketTrade(CorporationId,Time)"
        );
    }
}
