using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotInventoryStore(WorldDatabase database) : IPilotInventory
{
    public IReadOnlyList<InventoryItem> List(long pilotId, long locationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return world.Set<OwnedItem>().AsNoTracking().Where(i => i.OwnerId == pilotId && i.LocationId == locationId)
            .OrderBy(i => i.Id).AsEnumerable().Select(i => Read(catalog, i)).ToArray();
    }

    public IReadOnlyList<InventoryChange> Move(long pilotId, IReadOnlyList<long> itemIds, long sourceId,
        long destinationId, int flagId, int? quantity)
    {
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (ship.Position is not null ||
            !((sourceId == ship.Id && destinationId == ship.StationId && flagId == 4) ||
              (sourceId == ship.StationId && destinationId == ship.Id && flagId == 5)) ||
            itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count ||
            quantity is <= 0 || (quantity.HasValue && itemIds.Count != 1))
        {
            throw new InvalidDataException("Items can only move between your docked ship's cargo and its station hangar.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var items = world.Set<OwnedItem>().Where(i => itemIds.Contains(i.Id) && i.OwnerId == pilotId &&
            i.LocationId == sourceId && i.FlagId == (sourceId == ship.Id ? 5 : 4)).OrderBy(i => i.Id).ToArray();
        if (items.Length != itemIds.Count || items.Any(i => quantity > i.Quantity))
        {
            throw new InvalidDataException("The requested items or quantity are unavailable.");
        }
        if (destinationId == ship.Id)
        {
            double capacity = catalog.Set<ItemType>().Single(t => t.Id == ship.TypeId).Capacity ?? 0;
            double used = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship.Id && i.FlagId == 5)
                .AsEnumerable().Sum(i => Read(catalog, i).Volume * i.Quantity);
            double added = items.Sum(i => Read(catalog, i).Volume * (quantity ?? i.Quantity));
            if (added > capacity - used + 1e-9)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used), added);
            }
        }

        List<InventoryChange> changes = [];
        long nextId = NextId(world);
        foreach (var item in items)
        {
            var previous = Read(catalog, item);
            int moved = quantity ?? item.Quantity;
            if (moved < item.Quantity)
            {
                item.Quantity -= moved;
                changes.Add(new(Read(catalog, item), previous));
                var split = world.Set<OwnedItem>().Add(new OwnedItem
                {
                    Id = nextId++,
                    OwnerId = pilotId,
                    TypeId = item.TypeId,
                    LocationId = destinationId,
                    FlagId = flagId,
                    Quantity = moved,
                }).Entity;
                changes.Add(new(Read(catalog, split), null));
            }
            else
            {
                item.LocationId = destinationId;
                item.FlagId = flagId;
                changes.Add(new(Read(catalog, item), previous));
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    private static InventoryItem Read(CatalogDbContext catalog, OwnedItem item)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
        double volume = catalog.Set<SourceRecord>().Find("client/types", item.TypeId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientTypes>().Volume ?? throw new InvalidDataException($"Missing volume for item type {item.TypeId}.");
        if (!double.IsFinite(volume) || volume < 0)
        {
            throw new InvalidDataException($"Invalid volume for item type {item.TypeId}.");
        }
        return new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.FlagId, item.Quantity,
            type.GroupId, type.Group.CategoryId, volume);
    }

    private static long NextId(WorldDbContext world) => (world.Set<OwnedItem>().Max(i => (long?)i.Id) ?? 2000000000000) + 1;

    internal static void GrantStarterEquipment(WorldDbContext world, long pilotId, int stationId)
    {
        // A local starter grant, once per pilot. Future mining/fitting uses the catalog's Civilian Miner.
        long id = NextId(world);
        world.Database.ExecuteSql($"INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity) VALUES ({id},{pilotId},3651,{stationId},4,1)");
    }
}
