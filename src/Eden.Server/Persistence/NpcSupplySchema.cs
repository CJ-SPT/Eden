using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

internal static class NpcSupplySchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMarketOrder')")
                .AsEnumerable()
                .Contains("Kind")
        )
        {
            // Kind zero preserves every historical order as development supply.
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMarketOrder ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0");
        }
        world.Database.ExecuteSqlRaw(
            """
            DROP INDEX IF EXISTS IX_WorldMarketOrder_StationId_TypeId_Bid;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldMarketOrder_StationId_TypeId_Bid_Kind
                ON WorldMarketOrder(StationId,TypeId,Bid,Kind);
            """
        );
    }
}
