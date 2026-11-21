using Eden.Gameplay.Market;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class DevelopmentMarketOrderMigration
{
    internal const int Version = 191;

    internal static void RemoveUnusedAsks(WorldDbContext world)
    {
        // Catalog defaults can be reconstructed. Preserve finite demand, changed inventory, and every executed order ID.
        int removed = world.Database.ExecuteSql(
            $"""
            DELETE FROM WorldMarketOrder
            WHERE Kind={(int)NpcMarketKind.Development} AND Bid=0 AND SourceOrderId=0
                AND Issued>0 AND Remaining=Entered
                AND Id NOT IN (SELECT OrderId FROM WorldMarketTrade)
                AND Id NOT IN (SELECT OrderId FROM WorldRegionalMarketStock);
            """
        );
        if (removed > 0)
        {
            world.Database.ExecuteSql(
                $"""
                UPDATE WorldMarketStation SET SeedVersion={TestingMarketRules.SelectedTypesSeedVersion}
                WHERE SeedVersion={TestingMarketRules.SeedVersion};
                """
            );
        }
    }
}
