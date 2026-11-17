using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal static class StandardMarketSchema
{
    internal static void Upgrade(WorldDbContext world)
    {
        var columns = world
            .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMarketOrder')")
            .AsEnumerable()
            .ToHashSet();
        if (columns.Count == 0)
        {
            return;
        }
        if (!columns.Contains("Kind"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMarketOrder ADD COLUMN Kind INTEGER NOT NULL DEFAULT 0");
        }
        if (!columns.Contains("SourceOrderId"))
        {
            world.Database.ExecuteSqlRaw(
                """
                ALTER TABLE WorldMarketOrder ADD COLUMN SourceOrderId INTEGER NOT NULL DEFAULT 0;
                ALTER TABLE WorldMarketOrder ADD COLUMN DurationDays INTEGER NOT NULL DEFAULT 365;
                ALTER TABLE WorldMarketOrder ADD COLUMN Range INTEGER NOT NULL DEFAULT -1;
                ALTER TABLE WorldMarketOrder ADD COLUMN MinimumVolume INTEGER NOT NULL DEFAULT 1;
                """
            );
        }
        var stationColumns = world
            .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldMarketStation')")
            .AsEnumerable()
            .ToHashSet();
        if (!stationColumns.Contains("StandardSnapshotTicks"))
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldMarketStation ADD COLUMN StandardSnapshotTicks INTEGER NOT NULL DEFAULT 0");
        }
        world.Database.ExecuteSqlRaw(
            """
            DROP INDEX IF EXISTS IX_WorldMarketOrder_StationId_TypeId_Bid_Kind;
            CREATE UNIQUE INDEX IF NOT EXISTS IX_WorldMarketOrder_StationId_TypeId_Bid_Kind_SourceOrderId
                ON WorldMarketOrder(StationId,TypeId,Bid,Kind,SourceOrderId);
            """
        );
    }
}
