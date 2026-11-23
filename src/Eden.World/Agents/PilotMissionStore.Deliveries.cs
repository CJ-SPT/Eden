using Eden.Data.Catalog.Agents;
using Eden.Data.World.Agents;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Agents;
using Eden.Gameplay.Inventory;
using Eden.World.Catalog;
using Eden.World.Inventory;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;

namespace Eden.World.Agents;

internal sealed partial class PilotMissionStore
{
    public IReadOnlyList<InventoryChange> Deposit(long pilotId, long shipId, long containerId, InventoryMoveRequest request) =>
        LiveWorld.Execute(database.Path, () => DepositOwned(pilotId, shipId, containerId, request), nameof(Deposit));

    private IReadOnlyList<InventoryChange> DepositOwned(long pilotId, long shipId, long containerId, InventoryMoveRequest request)
    {
        if (
            request.SourceId != shipId
            || request.FlagId is not ((int)InventoryFlag.Cargo or (int)InventoryFlag.None)
            || request.ItemIds.Count == 0
            || request.ItemIds.Count != request.ItemIds.Distinct().Count()
            || request.Quantity is <= 0
            || request.Quantity.HasValue && request.ItemIds.Count != 1
        )
        {
            throw new InvalidDataException("Choose cargo from your active ship to deliver.");
        }
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Mission deliveries require the resident world.");
        using var world = live.Economy.Begin(new(pilots: [pilotId], ships: [shipId]));
        using CatalogDbContext catalog = new(database.CatalogPath);
        var access = AccessContainer(world, catalog, pilotId, containerId, Now);
        if (
            access.Pilot.ActiveShipId != shipId
            || access.Mission is not { } mission
            || access.Definition is not { DepositTypeId: > 0 } delivery
        )
        {
            throw new InvalidDataException("This mission container cannot receive cargo from this ship.");
        }
        world.RequireUncloaked(shipId);
        if (
            delivery.DepositRequiresDestroyedObjectId is long structureId
            && !world
                .Filter<SavedMissionStructure>(row => row.MissionId == mission.Id && row.ObjectId == structureId)
                .Any(row => row.DestroyedAt != null)
        )
        {
            throw new InvalidDataException("Destroy the mission target before delivering the cargo.");
        }
        if (DeliveryReceived(world, mission, containerId, delivery))
        {
            throw new InvalidDataException("This mission delivery is already complete.");
        }
        var items = request.ItemIds.Select(id => world.Set<OwnedItem>().Find(id)).OfType<OwnedItem>().ToArray();
        if (
            items.Length != request.ItemIds.Count
            || items.Any(item =>
                item.OwnerId != pilotId
                || item.LocationId != shipId
                || !ShipHoldRules.IsHold(item.FlagId)
                || item.TypeId != delivery.DepositTypeId
                || request.Quantity > item.Quantity
            )
            || items.Sum(item => (long)(request.Quantity ?? item.Quantity)) != delivery.DepositQuantity
        )
        {
            throw new InvalidDataException("Deliver the exact item and quantity requested by this mission.");
        }
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var previous = PilotInventoryStore.Read(catalog, item);
            int quantity = item.Quantity - (request.Quantity ?? item.Quantity);
            if (quantity == 0)
            {
                world.Remove(item);
            }
            else
            {
                item.Quantity = quantity;
            }
            changes.Add(new(previous with { Quantity = quantity }, previous));
        }
        // The container retains an inaccessible receipt. Closing the mission clears it along with any output cargo.
        int flag = (int)InventoryFlag.Cargo;
        InventoryPersistence.MergeOutput(world, catalog, pilotId, containerId, flag, delivery.DepositTypeId, delivery.DepositQuantity);
        if (delivery.TypeId > 0)
        {
            changes.Add(InventoryPersistence.MergeOutput(world, catalog, pilotId, containerId, flag, delivery.TypeId, delivery.Quantity));
        }
        foreach (var extra in delivery.AdditionalItems)
        {
            changes.Add(InventoryPersistence.MergeOutput(world, catalog, pilotId, containerId, flag, extra.TypeId, extra.Quantity));
        }
        var profile = MissionCatalog.Find(catalog, mission.MissionId);
        SpawnTriggeredWaves(world, catalog, mission, profile, MissionWaveTrigger.Delivery, delivery.ObjectId, Now);
        if (delivery.DepositCompletesMission)
        {
            mission.ObjectiveComplete = profile
                .Definition.Containers.Select((container, index) => (container, index))
                .Where(entry => entry.container.DepositTypeId > 0 && entry.container.DepositCompletesMission)
                .All(entry => DeliveryReceived(world, mission, MissionContainerRules.Id(mission.Id, entry.index), entry.container));
        }
        world.Commit();
        return changes;
    }

    private static bool MissionDeliveryComplete(WorldAction world, MissionProfile profile, SavedMission mission, long originalObjectId)
    {
        for (int index = 0; index < profile.Definition.Containers.Length; index++)
        {
            var container = profile.Definition.Containers[index];
            if (container.ObjectId == originalObjectId && container.DepositTypeId > 0)
            {
                return DeliveryReceived(world, mission, MissionContainerRules.Id(mission.Id, index), container);
            }
        }
        return false;
    }

    private static bool DeliveryReceived(WorldAction world, SavedMission mission, long containerId, MissionContainerDefinition delivery) =>
        world
            .Filter<OwnedItem>(item => item.LocationId == containerId && item.OwnerId == mission.PilotId)
            .Any(item => item.TypeId == delivery.DepositTypeId && item.Quantity == delivery.DepositQuantity);
}
