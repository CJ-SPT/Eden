using Eden.Data.Market;
using Eden.Gameplay.Economy;
using Eden.Gameplay.Market;
using Eden.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Storage.Migrations;

internal static class InitialEconomyMigration
{
    internal static void Initialize(
        WorldDbContext world,
        int version,
        Eden.Gameplay.Market.WorldEconomySettings? economy,
        Eden.Gameplay.Economy.EconomicConfiguration? economics
    )
    {
        if ((economy is not null || economics is not null) && version != 0)
        {
            throw new InvalidDataException("Economic starting settings require a new world.");
        }
        // Create before old funding migrations; existing worlds keep development defaults.
        PersonalMarketSchema.Upgrade(world);
        if (!world.Set<SavedWorldEconomy>().Any())
        {
            world.Add(
                new SavedWorldEconomy
                {
                    Id = Eden.Gameplay.Market.WorldEconomySettings.SingletonId,
                    TestingMarket = economy?.TestingMarket ?? true,
                    TestingGrants = economy?.TestingGrants ?? true,
                    InstitutionalStock = economy?.InstitutionalStock ?? Eden.Gameplay.Market.NpcSupplyRules.Stock,
                }
            );
            world.SaveChanges();
        }
    }
}
