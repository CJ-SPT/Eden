using System.Globalization;
using Eden.Protocol;
using Eden.Protocol.Combat;
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
            !((sourceId == ship.Id && destinationId == ship.StationId && flagId == (int)InventoryFlag.Hangar) ||
              ((sourceId == ship.StationId || sourceId == ship.Id) && destinationId == ship.Id &&
                flagId is (int)InventoryFlag.Cargo or (int)InventoryFlag.GeneralMiningHold)) ||
            itemIds.Count == 0 || itemIds.Distinct().Count() != itemIds.Count ||
            quantity is <= 0 || (quantity.HasValue && itemIds.Count != 1))
        {
            throw new InvalidDataException("Items can only move between your docked ship's cargo and its station hangar.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var items = world.Set<OwnedItem>().Where(i => itemIds.Contains(i.Id) && i.OwnerId == pilotId &&
            i.LocationId == sourceId && (sourceId == ship.Id ? i.FlagId == (int)InventoryFlag.Cargo || i.FlagId == (int)InventoryFlag.GeneralMiningHold ||
                (i.FlagId >= (int)InventoryFlag.MediumSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7) : i.FlagId == (int)InventoryFlag.Hangar)).OrderBy(i => i.Id).ToArray();
        if (items.Length != itemIds.Count || items.Any(i => quantity > i.Quantity))
        {
            throw new InvalidDataException("The requested items or quantity are unavailable.");
        }
        var unfittedIds = items.Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)).Select(i => i.Id).ToArray();
        var magazines = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && unfittedIds.Contains(i.LocationId)).OrderBy(i => i.Id).ToArray();
        if (destinationId == ship.Id)
        {
            double capacity = HoldCapacity(catalog, ship.TypeId, flagId);
            if (flagId == (int)InventoryFlag.GeneralMiningHold && (capacity <= 0 || items.Any(i => Read(catalog, i).CategoryId != (int)InventoryCategory.Asteroid)))
            {
                throw new InvalidDataException("Only mined ore can enter this ship's mining hold.");
            }
            double used = world.Set<OwnedItem>().Where(i => i.OwnerId == pilotId && i.LocationId == ship.Id && i.FlagId == flagId)
                .AsEnumerable().Sum(i => Read(catalog, i).Volume * i.Quantity);
            double added = items.Where(i => i.LocationId != ship.Id || i.FlagId != flagId).Sum(i => Read(catalog, i).Volume * (quantity ?? i.Quantity));
            added += magazines.Sum(i => Read(catalog, i).Volume * i.Quantity);
            if (added > capacity - used + InventoryDefinitions.ResourceTolerance)
            {
                throw new CargoCapacityException(Math.Max(0, capacity - used), added);
            }
        }

        List<InventoryChange> changes = [];
        // Unfitting returns the real loaded rounds as ordinary items in the same
        // destination, atomically with the module and its capacity check.
        foreach (var magazine in magazines)
        {
            var previous = Read(catalog, magazine);
            magazine.LocationId = destinationId;
            magazine.FlagId = flagId;
            changes.Add(new(Read(catalog, magazine), previous));
        }
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
                    Singleton = item.Singleton,
                }).Entity;
                changes.Add(new(Read(catalog, split), null));
            }
            else
            {
                item.LocationId = destinationId;
                item.FlagId = flagId;
                item.Online = false;
                changes.Add(new(Read(catalog, item), previous));
            }
        }
        world.SaveChanges();
        transaction.Commit();
        return changes;
    }

    internal static InventoryItem Read(CatalogDbContext catalog, OwnedItem item)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
        return new(item.Id, item.TypeId, item.OwnerId, item.LocationId, item.FlagId, item.Quantity,
            type.GroupId, type.Group.CategoryId, Volume(catalog, item.TypeId), item.Singleton, item.Online);
    }

    internal static double HoldCapacity(CatalogDbContext catalog, int typeId, int flagId) => flagId == (int)InventoryFlag.GeneralMiningHold
        ? catalog.Set<TypeAttribute>().Find(typeId, (int)DogmaAttributeId.GeneralMiningHoldCapacity)?.Value ?? 0
        : catalog.Set<ItemType>().Single(t => t.Id == typeId).Capacity ?? 0;

    internal static double Volume(CatalogDbContext catalog, int typeId)
    {
        double volume = catalog.Set<SourceRecord>().Find("client/types", typeId.ToString(CultureInfo.InvariantCulture))
            ?.Read<ClientTypes>().Volume ?? throw new InvalidDataException($"Missing volume for item type {typeId}.");
        if (!double.IsFinite(volume) || volume < 0)
        {
            throw new InvalidDataException($"Invalid volume for item type {typeId}.");
        }
        return volume;
    }

    internal static long NextId(WorldDbContext world)
    {
        // Packaged items become assembled ships without changing identity. Both
        // AUTOINCREMENT sequences retain their high-water IDs after deletion.
        long last = world.Database.SqlQueryRaw<long>("SELECT COALESCE(MAX(seq),0) AS Value FROM sqlite_sequence WHERE name IN ('WorldItem','WorldShip')")
            .AsEnumerable().Single();
        return checked(Math.Max(last, InventoryDefinitions.FirstOwnedItemId) + 1);
    }

    internal static void GrantStarterEquipment(WorldDbContext world, long pilotId, int stationId)
    {
        GrantStarterMiner(world, pilotId, stationId);
        GrantStarterWeapon(world, pilotId, stationId);
    }

    internal static void GrantStarterWeapon(WorldDbContext world, long pilotId, int stationId)
    {
        long id = NextId(world);
        int type = (int)CombatWeaponType.CivilianGatlingRailgun;
        world.Database.ExecuteSql($"INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity) VALUES ({id},{pilotId},{type},{stationId},{(int)InventoryFlag.Hangar},1)");
    }

    internal static void GrantStarterMiner(WorldDbContext world, long pilotId, int stationId)
    {
        // A local starter grant, once per pilot, using the catalog's Civilian Miner.
        long id = NextId(world);
        world.Database.ExecuteSql($"INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity) VALUES ({id},{pilotId},{InventoryDefinitions.CivilianMinerType},{stationId},{(int)InventoryFlag.Hangar},1)");
    }
}
