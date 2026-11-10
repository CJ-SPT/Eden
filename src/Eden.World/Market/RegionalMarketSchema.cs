using Eden.Data.World.Market;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal static class RegionalMarketSchema
{
    internal static void Configure(ModelBuilder model)
    {
        model.Entity<SavedRegionalMarketStock>().ToTable("WorldRegionalMarketStock").HasKey(s => s.OrderId);
        model.Entity<SavedRegionalMarketStock>().HasOne<NpcMarketOrder>().WithMany().HasForeignKey(s => s.OrderId);
        model.Entity<SavedMarketUnlock>().ToTable("WorldMarketUnlock").HasKey(s => s.TypeId);
    }

    internal static void Upgrade(WorldDbContext world)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldRegionalMarketStock(
                OrderId INTEGER PRIMARY KEY REFERENCES WorldMarketOrder(Id),
                AnchorCents INTEGER NOT NULL CHECK(AnchorCents>0),
                RefreshedTicks INTEGER NOT NULL CHECK(RefreshedTicks>=0));
            """
        );
        if (
            !world
                .Database.SqlQueryRaw<string>("SELECT name AS Value FROM pragma_table_info('WorldRegionalMarketStock')")
                .Contains(nameof(SavedRegionalMarketStock.DemandVersion))
        )
        {
            world.Database.ExecuteSqlRaw("ALTER TABLE WorldRegionalMarketStock ADD COLUMN DemandVersion INTEGER NOT NULL DEFAULT 0");
        }
    }
}
