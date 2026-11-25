using System.Text.Json;
using Eden.Data.World.Corporations;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Contracts;
using Eden.Gameplay.Corporations;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Industry;
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
            if (!CorporationStorageRules.CanQueryMarketDeliveries(CorporationRoleStore.Effective(world, actorId, corporation).Roles))
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

    public IReadOnlyList<InventoryChange> DeliverToMember(long actorId, CorporationMarketDelivery request)
    {
        if (
            request.ItemIds.Count is 0 or > CorporationStorageRules.MaximumBatch
            || request.ItemIds.Any(id => id <= 0)
            || request.ItemIds.Distinct().Count() != request.ItemIds.Count
            || request.Quantity is <= 0
            || request.Quantity.HasValue && request.ItemIds.Count != 1
        )
        {
            throw new InvalidDataException("Choose distinct corporate delivery items and a valid split quantity.");
        }
        var resident =
            LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Corporation deliveries require an active resident world.");
        using var world = resident.Economy.Begin(new(pilots: [actorId, request.RecipientId]));
        int station = DockedStation(world, actorId);
        var corporation = CorporationRoleStore.Current(world, actorId);
        if (request.StationId != station || request.CorporationId != corporation.Id)
        {
            throw new InvalidDataException("Withdraw your corporation's deliveries from the station where you are docked.");
        }
        if (!CorporationStorageRules.CanTakeMarketDeliveries(CorporationRoleStore.Effective(world, actorId, corporation).Roles))
        {
            throw new InvalidDataException("Accountant or Trader authority is required to withdraw Market Deliveries.");
        }
        if (world.Pilots.Find(request.RecipientId)?.CorporationId != corporation.Id)
        {
            throw new InvalidDataException("Deliver items to a current member of your corporation.");
        }
        using CatalogDbContext catalog = new(database.CatalogPath);
        List<InventoryChange> changes = [];
        foreach (long id in request.ItemIds)
        {
            var item = world.Set<OwnedItem>().Find(id);
            if (
                item is not { Quantity: > 0, Online: false }
                || item.OwnerId != corporation.Id
                || item.LocationId != station
                || item.FlagId != CorporationStorageRules.MarketDeliveriesFlag
                || item.TypeId == CourierRules.PackageType
                || request.Quantity > item.Quantity
                || PilotIndustryStore.IsInstalled(world, id)
                || world.Filter<OwnedItem>(child => child.LocationId == id).Any()
                || world.Filter<Ship>(child => child.StoredInId == id).Any()
            )
            {
                throw new InvalidDataException("A requested asset is unavailable in the local Market Deliveries hangar.");
            }
            var before = PilotInventoryStore.Read(catalog, item);
            if (request.Quantity is int quantity && quantity < item.Quantity)
            {
                item.Quantity = InventoryStackRules.Split(before, quantity).Remainder;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                item = world
                    .Add(
                        new OwnedItem
                        {
                            Id = InventoryPersistence.NextId(world),
                            TypeId = item.TypeId,
                            Quantity = quantity,
                            OwnerId = request.RecipientId,
                            LocationId = station,
                            FlagId = (int)InventoryFlag.Hangar,
                            BlueprintRuns = item.BlueprintRuns,
                            MaterialEfficiency = item.MaterialEfficiency,
                            TimeEfficiency = item.TimeEfficiency,
                            VolumeOverride = item.VolumeOverride,
                            CrystalDamage = item.CrystalDamage,
                        }
                    )
                    .Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), null));
            }
            else
            {
                item.OwnerId = request.RecipientId;
                item.FlagId = (int)InventoryFlag.Hangar;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
        }
        if (request.RecipientId != actorId)
        {
            var delivered = changes
                .Where(change => change.Item.OwnerId == request.RecipientId)
                .Select(change => change with { Previous = null })
                .ToArray();
            world.Add(
                new SavedCorporationChange
                {
                    PilotId = request.RecipientId,
                    StorageJson = JsonSerializer.Serialize(
                        new CorporationStorageChange(actorId, null, false, delivered, MarketDelivery: true)
                    ),
                }
            );
        }
        world.Commit();
        return changes;
    }
}
