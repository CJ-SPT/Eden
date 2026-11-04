using System.Globalization;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Protocol;
using Eden.Protocol.Combat;
using Eden.Protocol.Docking;
using Eden.Server.Catalog;
using Microsoft.EntityFrameworkCore;

namespace Eden.Server.Persistence;

[Injectable]
internal sealed class PilotInventoryStore(WorldDatabase database, TimeProvider clock) : IPilotInventory
{
    public IReadOnlyList<InventoryItem> List(long pilotId, long locationId)
    {
        using WorldDbContext world = new(database.Path);
        using CatalogDbContext catalog = new(database.CatalogPath);
        return List(world, catalog, pilotId, locationId);
    }

    internal static IReadOnlyList<InventoryItem> List(WorldDbContext world, CatalogDbContext catalog, long pilotId, long locationId)
    {
        var items = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && i.LocationId == locationId)
            .OrderBy(i => i.Id)
            .AsEnumerable()
            .Select(i => Read(catalog, i));
        return items
            .Concat(
                world
                    .Ships.Include(s => s.Position)
                    .Where(s => s.OwnerId == pilotId && s.StoredInId == locationId)
                    .AsEnumerable()
                    .Select(s => PilotFleetStore.Read(catalog, s))
            )
            .OrderBy(i => i.Id)
            .ToArray();
    }

    public IReadOnlyList<InventoryChange> Move(long pilotId, long destinationId, InventoryMoveRequest request)
    {
        var (itemIds, sourceId, flagId, quantity) = request;
        using WorldDbContext world = new(database.Path);
        using var transaction = world.Database.BeginTransaction();
        var pilot = world.Pilots.Include(p => p.ActiveShip).ThenInclude(s => s.Position).Single(p => p.Id == pilotId);
        var ship = pilot.ActiveShip;
        if (
            ship.Position is not null
            || !(
                (sourceId == ship.Id && destinationId == ship.StationId && flagId == (int)InventoryFlag.Hangar)
                || ((sourceId == ship.StationId || sourceId == ship.Id) && destinationId == ship.Id && ShipHoldRules.IsHold(flagId))
            )
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || quantity is <= 0
            || (quantity.HasValue && itemIds.Count != 1)
        )
        {
            throw new InvalidDataException("Items can only move between your docked ship's cargo and its station hangar.");
        }

        using CatalogDbContext catalog = new(database.CatalogPath);
        var holdFlags = ShipHoldRules.Definitions.Select(h => (int)h.Flag).ToArray();
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
                itemIds.Contains(i.Id)
                && i.OwnerId == pilotId
                && i.LocationId == sourceId
                && (
                    sourceId == ship.Id
                        ? holdFlags.Contains(i.FlagId)
                            || (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                        : i.FlagId == (int)InventoryFlag.Hangar
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();
        var storedShips = world
            .Ships.Include(s => s.Position)
            .Where(s => itemIds.Contains(s.Id) && s.OwnerId == pilotId)
            .AsEnumerable()
            .Where(s =>
                s.Position is null && (sourceId == ship.Id ? s.StoredInId == ship.Id : s.StoredInId is null && s.StationId == sourceId)
            )
            .OrderBy(s => s.Id)
            .ToArray();
        if (
            items.Length + storedShips.Length != itemIds.Count
            || items.Any(i => quantity > i.Quantity)
            || storedShips.Any(s => quantity is > 1 || world.Pilots.Any(p => p.ActiveShipId == s.Id))
        )
        {
            throw new InvalidDataException("The requested items or quantity are unavailable.");
        }
        if (items.Any(i => PilotIndustryStore.IsInstalled(world, i.Id)))
        {
            throw new InvalidDataException("Deliver or cancel the manufacturing job before moving its blueprint.");
        }
        var unfittedIds = items.Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)).Select(i => i.Id).ToArray();
        long now = clock.GetUtcNow().UtcDateTime.ToFileTimeUtc();
        var checkpoint = unfittedIds.Length > 0 ? FittingChanges.Capture(world, catalog, pilot, now) : null;
        var magazines = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && unfittedIds.Contains(i.LocationId))
            .OrderBy(i => i.Id)
            .ToArray();
        if (destinationId == ship.Id)
        {
            double capacity = HoldCapacity(world, catalog, pilotId, ship.Id, flagId);
            var hold = ShipHoldRules.Definition(flagId)!;
            if (
                (capacity <= 0 && flagId != (int)InventoryFlag.Cargo)
                || items.Concat(magazines).Any(i => !hold.Allows(Read(catalog, i)))
                || storedShips.Any(s => !hold.AllowsAssembledShip || !hold.Allows(PilotFleetStore.Read(catalog, s)))
            )
            {
                throw new InvalidDataException("This hold is unavailable or does not permit the requested cargo.");
            }
            foreach (var contained in storedShips)
            {
                if (hold.Flag == InventoryFlag.FrigateEscapeBay)
                {
                    PilotSkillReader.CheckRequirements(world, catalog, pilotId, contained.TypeId);
                }
                ShipStorageRules.CheckContents(
                    List(world, catalog, pilotId, contained.Id),
                    world.Ships.Any(s => s.StoredInId == contained.Id)
                );
            }
            double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, ship.Id, flagId);
            double added = items
                .Where(i => i.LocationId != ship.Id || i.FlagId != flagId)
                .Sum(i => hold.RequiredCapacity(Read(catalog, i), quantity ?? i.Quantity));
            added += magazines.Sum(i => hold.RequiredCapacity(Read(catalog, i), i.Quantity));
            added += storedShips
                .Where(s => s.StoredInId != ship.Id || s.StorageFlagId != flagId)
                .Sum(s => hold.RequiredCapacity(PilotFleetStore.Read(catalog, s), 1));
            if (
                flagId == (int)InventoryFlag.FrigateEscapeBay
                && world.Ships.Count(s => s.StoredInId == ship.Id && s.StorageFlagId == flagId && !itemIds.Contains(s.Id))
                    + storedShips.Length
                    > 1
            )
            {
                throw new InvalidDataException("The frigate escape bay holds one assembled frigate.");
            }
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
        long nextId = InventoryPersistence.NextId(world);
        foreach (var item in items)
        {
            var previous = Read(catalog, item);
            int moved = quantity ?? item.Quantity;
            if (moved < item.Quantity)
            {
                item.Quantity -= moved;
                changes.Add(new(Read(catalog, item), previous));
                var split = world
                    .Set<OwnedItem>()
                    .Add(
                        new OwnedItem
                        {
                            Id = nextId++,
                            OwnerId = pilotId,
                            TypeId = item.TypeId,
                            LocationId = destinationId,
                            FlagId = flagId,
                            Quantity = moved,
                            Singleton = item.Singleton,
                            CrystalDamage = item.CrystalDamage,
                        }
                    )
                    .Entity;
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
        foreach (var stored in storedShips)
        {
            var previous = PilotFleetStore.Read(catalog, stored);
            stored.StoredInId = destinationId == ship.Id ? ship.Id : null;
            stored.StorageFlagId = flagId;
            if (stored.StoredInId is null)
            {
                stored.StationId = checked((int)destinationId);
            }
            changes.Add(new(PilotFleetStore.Read(catalog, stored), previous));
        }
        world.SaveChanges();
        if (checkpoint is not null)
        {
            changes.AddRange(FittingChanges.Finish(world, catalog, pilot, checkpoint, now));
        }
        transaction.Commit();
        return changes;
    }

    internal static InventoryItem Read(CatalogDbContext catalog, OwnedItem item)
    {
        var type = catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == item.TypeId);
        return new(
            item.Id,
            item.TypeId,
            item.OwnerId,
            item.LocationId,
            item.FlagId,
            item.Quantity,
            type.GroupId,
            type.Group.CategoryId,
            Volume(catalog, item.TypeId, item.Singleton, type),
            item.Singleton,
            item.Online,
            item.CrystalDamage
        );
    }

    internal static double HoldCapacity(WorldDbContext world, CatalogDbContext catalog, long pilotId, long shipId, int flagId)
    {
        var pilot = world.Pilots.Include(p => p.ActiveShip).Single(p => p.Id == pilotId);
        var ship = world.Ships.Find(shipId) ?? throw new InvalidDataException("The ship is unavailable.");
        return ShipHoldRules.Capacity(flagId, PilotFittingStore.ShipAttributes(world, catalog, pilot, ship));
    }

    internal static double HoldCapacity(CatalogDbContext catalog, int typeId, int flagId)
    {
        var attributes = PilotFittingStore.ReadAttributes(catalog, typeId).ToDictionary();
        attributes[DogmaAttributeId.CargoCapacity] = catalog.Set<ItemType>().Find(typeId)?.Capacity ?? 0;
        return ShipHoldRules.Capacity(flagId, attributes);
    }

    internal static double Volume(CatalogDbContext catalog, int typeId, bool singleton = false, ItemType? type = null)
    {
        double volume =
            catalog.Set<SourceRecord>().Find("client/types", typeId.ToString(CultureInfo.InvariantCulture))?.Read<ClientTypes>().Volume
            ?? throw new InvalidDataException($"Missing volume for item type {typeId}.");
        if (!double.IsFinite(volume) || volume < 0)
        {
            throw new InvalidDataException($"Invalid volume for item type {typeId}.");
        }
        if (singleton)
        {
            return volume;
        }
        type ??= catalog.Set<ItemType>().Include(t => t.Group).Single(t => t.Id == typeId);
        return type.Group.CategoryId == (int)InventoryCategory.Ship
            ? ShipVolumeRules.Packaged(typeId, (HullClass)type.GroupId, volume)
            : volume;
    }

    internal static void GrantStarterEquipment(WorldDbContext world, long pilotId, int stationId)
    {
        GrantStarterMiner(world, pilotId, stationId);
        GrantStarterWeapon(world, pilotId, stationId);
    }

    internal static void GrantStarterWeapon(WorldDbContext world, long pilotId, int stationId)
    {
        long id = InventoryPersistence.NextId(world);
        int type = (int)CombatWeaponType.CivilianGatlingRailgun;
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity)
            VALUES ({id},{pilotId},{type},{stationId},{(int)InventoryFlag.Hangar},1)
            """
        );
    }

    internal static void GrantStarterMiner(WorldDbContext world, long pilotId, int stationId)
    {
        // A local starter grant, once per pilot, using the catalog's Civilian Miner.
        long id = InventoryPersistence.NextId(world);
        world.Database.ExecuteSql(
            $"""
            INSERT INTO WorldItem(Id,OwnerId,TypeId,LocationId,FlagId,Quantity)
            VALUES ({id},{pilotId},{InventoryDefinitions.CivilianMinerType},{stationId},{(int)InventoryFlag.Hangar},1)
            """
        );
    }
}
