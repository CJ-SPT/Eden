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
        var available = CatalogTypeLists.Read(catalog, PurchasableSkillsList);
        List<SkillPurchaseLine> purchases = [];
        foreach (int typeId in typeIds)
        {
            var item = catalog.FittingType(typeId);
            var type = catalog.Read(CatalogData.Client.Types, (long)typeId);
            long price = SkillPurchaseRules.PriceCents(
                new(
                    typeId,
                    (InventoryCategory)(item?.CategoryId ?? 0),
                    type?.Published == 1,
                    item is not null && available.Contains(typeId, item.GroupId, item.CategoryId),
                    type?.BasePrice ?? 0,
                    catalog.FittingAttributes(typeId).GetValueOrDefault(DogmaAttributeId.SkillTimeConstant),
                    world.Set<PilotSkill>().Find(pilotId, typeId) is not null
                )
            );
            purchases.Add(new(typeId, price));
        }
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        long balance = world.Set<PilotWallet>().Find(pilotId)?.Cents ?? 0;
        foreach (var purchase in purchases)
        {
            balance = EconomicStore.Pay(world, pilotId, 0, purchase.TypeId, WalletEntryType.SkillPurchase, purchase.PriceCents, now);
            world.Add(new PilotSkill { PilotId = pilotId, TypeId = purchase.TypeId });
        }
        if (purchases.Count > 0)
        {
            PilotTaskTrackingStore.Record(world, catalog, pilotId, now, TaskEvents.SkillInjected, purchases.Count);
        }
        transaction.Commit();
        return new(typeIds.ToArray(), balance);
    }
}
