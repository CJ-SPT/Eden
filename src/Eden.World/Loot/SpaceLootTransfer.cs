using Eden.Data.World.Characters;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Loot;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Inventory;
using Eden.World.Storage;

namespace Eden.World.Loot;

internal static partial class SpaceLootTransfer
{
    internal static IReadOnlyList<InventoryChange> Take(
        WorldDbContext world,
        CatalogDbContext catalog,
        Pilot pilot,
        long shipId,
        long containerId,
        IReadOnlyList<long> itemIds,
        int? quantity,
        InventoryFlag destinationFlag
    )
    {
        long pilotId = pilot.Id;
        if (
            pilot.ActiveShipId != shipId
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || quantity is <= 0
            || quantity.HasValue && itemIds.Count != 1
        )
        {
            throw new InvalidDataException("Select available container contents and a valid quantity for your active ship's cargo.");
        }
        var items = world
            .Set<OwnedItem>()
            .Where(i => itemIds.Contains(i.Id) && i.OwnerId == pilotId && i.LocationId == containerId)
            .OrderBy(i => i.Id)
            .ToArray();
        var storedShips = world
            .Ships.Where(s => itemIds.Contains(s.Id) && s.OwnerId == pilotId && s.StoredInId == containerId)
            .OrderBy(s => s.Id)
            .ToArray();
        if (
            items.Length + storedShips.Length != itemIds.Count
            || items.Any(i => i.Quantity <= 0 || quantity > i.Quantity)
            || (storedShips.Length > 0 && quantity is > 1)
        )
        {
            throw new InvalidDataException("The requested container contents are no longer available.");
        }
        var hold = ShipHoldRules.Definition((int)destinationFlag);
        double capacity = PilotInventoryStore.HoldCapacity(world, catalog, pilotId, pilot.ActiveShipId, (int)destinationFlag);
        if (
            hold is null
            || (capacity <= 0 && destinationFlag != InventoryFlag.Cargo)
            || items.Any(i => !hold.Allows(PilotInventoryStore.Read(catalog, i)))
            || storedShips.Any(s => !hold.AllowsAssembledShip || !hold.Allows(PilotFleetStore.Read(catalog, s)))
        )
        {
            throw new InvalidDataException("The destination hold cannot accept these container contents.");
        }
        if (destinationFlag == InventoryFlag.FrigateEscapeBay)
        {
            throw new InvalidDataException("Load the frigate escape bay while docked.");
        }
        double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, shipId, (int)destinationFlag);
        double added =
            items.Sum(i => PilotInventoryStore.Read(catalog, i).Volume * (quantity ?? i.Quantity))
            + storedShips.Sum(s => PilotInventoryStore.Volume(catalog, s.TypeId, singleton: true));
        if (added > capacity - used + InventoryDefinitions.ResourceTolerance)
        {
            throw new CargoCapacityException(Math.Max(0, capacity - used), added);
        }
        List<InventoryChange> changes = [];
        foreach (var item in items)
        {
            var before = PilotInventoryStore.Read(catalog, item);
            int moved = quantity ?? item.Quantity;
            if (moved < item.Quantity && JetcanRules.IsJetcan(containerId))
            {
                changes.AddRange(InventoryPersistence.SplitTransfer(world, catalog, item, before, shipId, (int)destinationFlag, moved));
            }
            else if (moved < item.Quantity)
            {
                item.Quantity -= moved;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
                var split = world
                    .Set<OwnedItem>()
                    .Add(
                        new()
                        {
                            Id = InventoryPersistence.NextId(world),
                            OwnerId = pilotId,
                            TypeId = item.TypeId,
                            LocationId = shipId,
                            FlagId = (int)destinationFlag,
                            Quantity = moved,
                        }
                    )
                    .Entity;
                changes.Add(new(PilotInventoryStore.Read(catalog, split), null));
            }
            else
            {
                item.LocationId = shipId;
                item.FlagId = (int)destinationFlag;
                item.Online = false;
                changes.Add(new(PilotInventoryStore.Read(catalog, item), before));
            }
        }
        foreach (var stored in storedShips)
        {
            var previous = PilotFleetStore.Read(catalog, stored);
            stored.StoredInId = shipId;
            stored.StorageFlagId = (int)destinationFlag;
            changes.Add(new(PilotFleetStore.Read(catalog, stored), previous));
        }
        return changes;
    }
}
