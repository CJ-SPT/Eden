using Eden.Gameplay.Economy;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Combat;
using Eden.World.Economy;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class WorldVersions86To92
{
    internal static void Apply(
        WorldDbContext world,
        CatalogDbContext catalog,
        TimeProvider clock,
        int version,
        Eden.Gameplay.Market.WorldEconomySettings? economy,
        Eden.Gameplay.Economy.EconomicConfiguration? economics
    )
    {
        if (version < 86)
        {
            HistorySchema.Upgrade(world);
        }
        PersonalMarketSchema.Upgrade(world);
        NpcSupplySchema.Upgrade(world);
        EconomicSchema.Upgrade(world);
        EconomicStore.Initialize(world, catalog, economy is { TestingMarket: false }, economics);
        CrimewatchSchema.Upgrade(world);
        world.Database.ExecuteSqlRaw("PRAGMA user_version=92");
    }
}
