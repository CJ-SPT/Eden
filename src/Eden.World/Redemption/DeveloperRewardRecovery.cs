using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Redemption;
using Eden.Gameplay.Skills;
using Eden.World.Catalog;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Redemption;

internal static class DeveloperRewardRecovery
{
    private sealed record RecoveryStack(OwnedItem Item, long AccountId);

    internal static void Reconcile(WorldDbContext world, CatalogDbContext catalog, long now)
    {
        // Earlier /create and /load calls put automatic rewards into ordinary
        // inventory. Move only personal, accessible stacks; leave escrow alone.
        int[] types = SkillPointRewards.Amounts.Keys.Concat(Enum.GetValues<RedeemCurrencyType>().Select(t => (int)t)).ToArray();
        var candidates = world
            .Set<OwnedItem>()
            .Where(i =>
                types.Contains(i.TypeId)
                && i.Quantity > 0
                && !i.Singleton
                && !i.Online
                && (i.FlagId == (int)InventoryFlag.Hangar || i.FlagId == (int)InventoryFlag.Cargo)
            )
            .Join(world.Pilots, item => item.OwnerId, pilot => pilot.Id, (item, pilot) => new RecoveryStack(item, pilot.AccountId))
            .ToArray();
        if (candidates.Length == 0)
        {
            return;
        }
        using var transaction = world.Database.BeginTransaction();
        foreach (var candidate in candidates)
        {
            var item = candidate.Item;
            bool accessible =
                item.FlagId == (int)InventoryFlag.Hangar
                    ? catalog.FindStation(item.LocationId) is not null
                    : world.Ships.Any(s => s.OwnerId == item.OwnerId && s.Id == item.LocationId);
            if (!accessible)
            {
                continue;
            }
            var type = catalog.FittingType(item.TypeId);
            if (type is null)
            {
                continue;
            }
            AccountRedemptionStore.Grant(
                world,
                catalog,
                new(candidate.AccountId, $"inventory-recovery/{item.Id}", item.TypeId, item.Quantity, type.Name),
                now
            );
            world.Remove(item);
        }
        world.SaveChanges();
        transaction.Commit();
    }
}
