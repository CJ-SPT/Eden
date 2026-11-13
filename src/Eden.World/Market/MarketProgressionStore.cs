using Eden.Data.World.Industry;
using Eden.Data.World.Market;
using Eden.Gameplay.Industry;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Market;

internal static class MarketProgressionStore
{
    internal static void Claim(WorldDbContext world, CatalogDbContext catalog, IndustryJob job)
    {
        long account = world.Pilots.Where(p => p.Id == job.PilotId).Select(p => p.AccountId).Single();
        if (
            account <= 0
            || job.Activity != IndustryActivity.Manufacturing
            || job.Status != IndustryJobStatus.Delivered
            || job.CompletedAt is null
            || job.OutputQuantity <= 0
            || RegionalMarketCatalog.Product(catalog, job.ProductTypeId) is null
            || world.Set<SavedMarketUnlock>().Find(job.ProductTypeId) is not null
        )
        {
            return;
        }
        // Commit with inventory delivery. Audit identities intentionally outlive
        // deleted characters, accounts and industry history.
        world.Add(
            new SavedMarketUnlock
            {
                TypeId = job.ProductTypeId,
                AccountId = account,
                PilotId = job.PilotId,
                JobId = job.Id,
                ClaimedAt = job.CompletedAt.Value,
            }
        );
    }

    internal static void RetirePlayerSuppliedInputs(WorldDbContext world, CatalogDbContext catalog)
    {
        var inputs = catalog
            .Definitions.Items.Values.Where(item =>
                RegionalMarketRules.IsPlayerSuppliedIndustryInput(item.Id, (InventoryCategory)item.CategoryId, item.GroupId)
            )
            .Select(item => item.Id)
            .ToArray();
        world
            .Set<NpcMarketOrder>()
            .Where(order => !order.Bid && order.Issued > 0 && inputs.Contains(order.TypeId))
            .ExecuteUpdate(setters => setters.SetProperty(o => o.Remaining, 0).SetProperty(o => o.Issued, 0));
    }

    internal static void Upgrade(WorldDbContext world, CatalogDbContext catalog)
    {
        world.Database.ExecuteSqlRaw(
            """
            CREATE TABLE IF NOT EXISTS WorldMarketUnlock(
                TypeId INTEGER PRIMARY KEY,
                AccountId INTEGER NOT NULL,
                PilotId INTEGER NOT NULL,
                JobId INTEGER NOT NULL,
                ClaimedAt INTEGER NOT NULL);
            """
        );
        foreach (
            var job in world
                .Set<IndustryJob>()
                .Where(j => j.Status == IndustryJobStatus.Delivered)
                .OrderBy(j => j.CompletedAt)
                .ThenBy(j => j.Id)
                .ToArray()
        )
        {
            Claim(world, catalog, job);
        }
        world.SaveChanges();
        // Preserve historical order IDs and executions, but retire time-unlocked
        // supply. Eligible products are reactivated from the new snapshot on read.
        world
            .Set<NpcMarketOrder>()
            .Where(o => o.Kind == Eden.Gameplay.Market.NpcMarketKind.Regional)
            .ExecuteUpdate(setters => setters.SetProperty(o => o.Remaining, 0).SetProperty(o => o.Issued, 0));
    }
}
