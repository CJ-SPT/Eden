using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;

namespace Eden.World.Corporations;

internal sealed partial class CorporationStorageStore
{
    public IReadOnlyList<InventoryItem> Deliveries(long actorId)
    {
        var resident =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation deliveries require an active resident world.");
        return resident.Economy.Inspect(world =>
        {
            int station = DockedStation(world, actorId);
            var corporation = CorporationRoleStore.Current(world, actorId);
            if (!CorporationRoleStore.Has(world, actorId, corporation, CorporationRole.DeliveriesHangarQuery))
            {
                throw new InvalidDataException("Corporation deliveries query authority is required.");
            }
            using CatalogDbContext catalog = new(database.CatalogPath);
            return PilotInventoryStore
                .List(world, catalog, corporation.Id, station)
                .Where(item => item.FlagId == CorporationStorageRules.MarketDeliveriesFlag)
                .ToArray();
        });
    }
}
