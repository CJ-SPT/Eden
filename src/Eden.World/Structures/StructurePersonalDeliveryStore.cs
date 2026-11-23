using Eden.Data.Catalog;
using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Notifications;
using Eden.Gameplay.Structures;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Industry;
using Eden.World.Inventory;
using Eden.World.Mail;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Storage;

namespace Eden.World.Structures;

[Injectable]
internal sealed class StructurePersonalDeliveryStore(WorldDatabase database, TimeProvider clock, InboxChangeSignal signal)
    : IStructurePersonalDelivery
{
    // Build 3662168 evetypes.const.TYPE_LIST_SOULBOUND.
    private const int SoulboundTypeList = 142;
    private static readonly CatalogDefinition<InventoryTypeList?> s_soulbound = new(
        "structure-delivery-soulbound",
        (catalog, id) => catalog.Read(CatalogData.Client.Typelist, (long)id) is null ? null : CatalogTypeLists.Read(catalog, id)
    );

    public IReadOnlyList<InventoryChange> Deliver(long senderId, long recipientId, long structureId, IReadOnlyList<long> itemIds)
    {
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidOperationException("Personal deliveries require the resident world.");
        using var world = live.Economy.Begin(new(pilots: [senderId, recipientId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        if (
            world.Pilots.Find(senderId) is not { } sender
            || sender.ActiveShip is not { Position: null, StoredInId: null } ship
            || ship.OwnerId != senderId
            || ship.StructureId != structureId
            || world.Set<SavedCharacterDeletion>().Find(senderId)?.DeletedAt is not null
        )
        {
            throw new InvalidDataException("Dock at the structure before delivering items.");
        }
        if (!world.PilotExists(recipientId) || world.Set<SavedCharacterDeletion>().Find(recipientId)?.DeletedAt is not null)
        {
            throw new InvalidDataException("Choose a local capsuleer to receive the delivery.");
        }
        var structure = PilotStructureStore.Require(world, structureId);
        var profile = StructureCatalog.Require(catalog, structure.TypeId);
        if (!StructureRules.CanDock(profile, structure.State, 0) || structure.UnanchorAt <= now)
        {
            throw new InvalidDataException("That structure cannot accept personal deliveries.");
        }

        var soulbound = catalog.Definition(s_soulbound, SoulboundTypeList);
        List<InventoryChange> changes = [];
        List<InventoryItem> accepted = [];
        foreach (long id in itemIds.Distinct())
        {
            if (
                id <= 0
                || world.Set<OwnedItem>().Find(id) is not { FlagId: (int)InventoryFlag.Hangar } root
                || root.OwnerId != senderId
                || root.LocationId != structureId
                || ReadLeg(world, catalog, root, soulbound) is not { } items
            )
            {
                continue;
            }
            accepted.Add(items[0].Snapshot);
            foreach (var (item, before) in items)
            {
                item.OwnerId = recipientId;
                if (item.Id == id)
                {
                    item.FlagId = (int)InventoryFlag.StructureDeliveries;
                }
                if (before.OwnerId != item.OwnerId || before.FlagId != item.FlagId)
                {
                    changes.Add(new(before with { OwnerId = item.OwnerId, FlagId = item.FlagId }, before));
                }
            }
        }

        if (accepted.Count > 0)
        {
            var quantities = accepted
                .GroupBy(item => item.TypeId)
                .OrderBy(group => group.Key)
                .Select(group => new StructureDeliveryQuantity(group.Key, group.Sum(item => item.Singleton ? 1L : item.Quantity)))
                .ToArray();
            StructureDeliveryContent notice = new(
                structure.Id,
                structure.TypeId,
                structure.SystemId,
                quantities,
                accepted.Select(i => i.Id).ToArray()
            );
            PilotInboxStore.AddStructureDelivery(world, senderId, recipientId, notice, now);
            world.AfterCommit(signal.Publish);
        }
        world.Commit();
        return changes;
    }

    private static List<(OwnedItem Item, InventoryItem Snapshot)>? ReadLeg(
        WorldAction world,
        CatalogDbContext catalog,
        OwnedItem root,
        InventoryTypeList? soulbound
    )
    {
        List<(OwnedItem, InventoryItem)> items = [];
        HashSet<long> visited = [];
        Queue<OwnedItem> pending = new([root]);
        while (pending.TryDequeue(out var item))
        {
            if (
                !visited.Add(item.Id)
                || item.OwnerId != root.OwnerId
                || item.Quantity <= 0
                || item.TypeId is CourierRules.PackageType or AssetSafetyRules.WrapTypeId
                || catalog.FittingType(item.TypeId) is not { } type
                || soulbound?.Contains(item.TypeId, type.GroupId, type.CategoryId) == true
                || PilotIndustryStore.IsInstalled(world, item.Id)
            )
            {
                return null;
            }
            var snapshot = PilotInventoryStore.Read(catalog, item);
            items.Add((item, snapshot));
            var children = world.ContainedInventoryIds(item.Id);
            if (children.Count != 0 && (!item.Singleton || item.Quantity != 1 || !CargoContainerAssemblyRules.IsContainer(snapshot)))
            {
                return null;
            }
            // Discover children without inventory filters so escrow and unsupported assembled hulls reject this entire leg.
            foreach (long id in children)
            {
                if (
                    world.Set<OwnedItem>().Find(id)
                    is not { FlagId: (int)InventoryFlag.None or (int)InventoryFlag.Locked or (int)InventoryFlag.Unlocked } child
                )
                {
                    return null;
                }
                pending.Enqueue(child);
            }
        }
        return items;
    }
}
