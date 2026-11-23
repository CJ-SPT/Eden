using Eden.Data.World.Market;
using Eden.Data.World.Skills;
using Eden.Gameplay.Characters;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Market;
using Eden.Gameplay.Skills;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Characters;
using Eden.World.Economy;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Skills;

[Injectable]
internal sealed class PilotSkillPurchaseStore(WorldDatabase database, TimeProvider clock) : IPilotSkillPurchases
{
    // Build 3662168 evetypes.const.TYPE_LIST_SKILLS_AVAILABLE_FOR_PURCHASE.
    private const long PurchasableSkillsList = 93;

    public SkillPurchase Purchase(long pilotId, IReadOnlyList<int> typeIds)
    {
        if (typeIds.Any(id => id <= 0) || typeIds.Distinct().Count() != typeIds.Count)
        {
            throw new InvalidDataException("Select distinct skill types to purchase.");
        }
        using WorldActionScope transaction = new(database, new(pilots: [pilotId]));
        var world = transaction.World;
        if (!world.PilotExists(pilotId))
        {
            throw new InvalidDataException("The pilot does not exist.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        var purchases = typeIds
            .Select(typeId => new SkillPurchaseLine(typeId, SkillPurchaseRules.PriceCents(Offer(world, catalog, pilotId, typeId))))
            .ToArray();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long balance = Settle(world, catalog, pilotId, purchases, now);
        transaction.Commit();
        return new(typeIds.ToArray(), balance);
    }

    internal static bool TryPurchase(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        int typeId,
        long budget,
        long now,
        bool fundedRefit = false
    )
    {
        if (catalog.Read(CatalogData.Client.Typelist, PurchasableSkillsList) is null)
        {
            return false;
        }
        var offer = Offer(world, catalog, pilotId, typeId);
        if (
            offer is not { Category: InventoryCategory.Skill, Published: true, Available: true, Injected: false, BasePrice: > 0, Rank: > 0 }
        )
        {
            return false;
        }
        long price = SkillPurchaseRules.PriceCents(offer);
        if (price > budget)
        {
            return false;
        }
        Settle(world, catalog, pilotId, [new(typeId, price)], now, fundedRefit);
        world.SaveChanges();
        return true;
    }

    private static SkillPurchaseOffer Offer(WorldAction world, CatalogDbContext catalog, long pilotId, int typeId)
    {
        var available = CatalogTypeLists.Read(catalog, PurchasableSkillsList);
        var item = catalog.FittingType(typeId);
        var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
        return new(
            typeId,
            (InventoryCategory)(item?.CategoryId ?? 0),
            type?.Published == 1,
            item is not null && available.Contains(typeId, item.GroupId, item.CategoryId),
            type?.BasePrice ?? 0,
            catalog.FittingAttributes(typeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant),
            world.Set<PilotSkill>().Find(pilotId, typeId) is not null
        );
    }

    private static long Settle(
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        IReadOnlyList<SkillPurchaseLine> purchases,
        long now,
        bool fundedRefit = false
    )
    {
        long balance = world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0;
        foreach (var purchase in purchases)
        {
            if (!fundedRefit)
            {
                balance = EconomicStore.Pay(world, pilotId, 0, purchase.TypeId, WalletEntryType.SkillPurchase, purchase.PriceCents, now);
            }
            world.Add(new PilotSkill { PilotId = pilotId, TypeId = purchase.TypeId });
        }
        if (purchases.Count > 0)
        {
            PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.SkillInjected, purchases.Count);
        }
        return balance;
    }
}
