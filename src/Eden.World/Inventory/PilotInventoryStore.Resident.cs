using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Inventory;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal sealed partial class PilotInventoryStore
{
    internal static IReadOnlyList<InventoryItem> List(EconomyTransaction world, CatalogDbContext catalog, long pilotId, long locationId)
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

    internal static IReadOnlyList<InventoryChange> Move(
        EconomyTransaction world,
        CatalogDbContext catalog,
        long pilotId,
        long destinationId,
        InventoryMoveRequest request,
        long now,
        IReadOnlyDictionary<DogmaAttributeId, double>? attributes = null
    )
    {
        attributes ??= world.Attributes(pilotId);
        var (itemIds, sourceId, flagId, quantity) = request;
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var ship = pilot.ActiveShip;
        world.RequireUncloaked(ship.Id);
        bool sameShipQuantityMove = sourceId == ship.Id && destinationId == ship.Id && quantity.HasValue;
        if (
            (ship.Position is not null && !sameShipQuantityMove)
            || !(
                (sourceId == ship.Id && destinationId == ship.DockedLocationId && flagId == (int)InventoryFlag.Hangar)
                || (
                    sourceId == ship.DockedLocationId
                    && destinationId == ship.DockedLocationId
                    && flagId == (int)InventoryFlag.Hangar
                    && quantity.HasValue
                )
                || ((sourceId == ship.DockedLocationId || sourceId == ship.Id) && destinationId == ship.Id && ShipHoldRules.IsHold(flagId))
            )
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || quantity is <= 0
            || (quantity.HasValue && itemIds.Count != 1)
        )
        {
            throw new InvalidDataException("Items can only move between your docked ship's cargo and its station hangar.");
        }

        var holdFlags = ShipHoldRules.Definitions.Select(h => (int)h.Flag).ToArray();
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
                Enumerable.Contains(itemIds, i.Id)
                && i.OwnerId == pilotId
                && i.LocationId == sourceId
                && (
                    sourceId == ship.Id
                        ? Enumerable.Contains(holdFlags, i.FlagId)
                            || (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                            || (i.FlagId >= (int)InventoryFlag.RigSlot0 && i.FlagId <= (int)InventoryFlag.RigSlot2)
                        : i.FlagId == (int)InventoryFlag.Hangar
                )
            )
            .OrderBy(i => i.Id)
            .ToArray();
        var storedShips = world
            .Ships.Include(s => s.Position)
            .Where(s => Enumerable.Contains(itemIds, s.Id) && s.OwnerId == pilotId)
            .AsEnumerable()
            .Where(s =>
                s.Position is null
                && (sourceId == ship.Id ? s.StoredInId == ship.Id : s.StoredInId is null && s.DockedLocationId == sourceId)
            )
            .OrderBy(s => s.Id)
            .ToArray();
        if (
            items.Length + storedShips.Length != itemIds.Count
            || items.Any(i => quantity > i.Quantity)
            || storedShips.Any(s => quantity is > 1 || world.Pilots.Any(p => p.ActiveShipId == s.Id))
            || (ship.Position is not null && (items.Any(item => item.FlagId != flagId) || storedShips.Length > 0))
        )
        {
            throw new InvalidDataException("The requested items or quantity are unavailable.");
        }
        if (items.Any(i => PilotIndustryStore.IsInstalled(world, i.Id)))
        {
            throw new InvalidDataException("Deliver or cancel the industry job before moving its blueprint.");
        }
        if (sourceId == destinationId && destinationId == ship.DockedLocationId && storedShips.Length > 0)
        {
            throw new InvalidDataException("Only packaged item stacks can be divided.");
        }
        if (sourceId == destinationId && quantity is int selected && items is [var stack] && stack.FlagId == flagId)
        {
            // Shift-drag sends Add(item, sameLocation, qty=..., flag=...). This is a split,
            // not a transfer; it keeps both rows in place and consumes no additional hold capacity.
            if (selected == stack.Quantity)
            {
                return [];
            }
            var splitChanges = SplitInPlace(world, catalog, stack, selected);
            world.SaveChanges();
            return splitChanges;
        }
        var unfittedIds = items.Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)).Select(i => i.Id).ToArray();
        if (unfittedIds.Length != 0)
        {
            throw new InvalidOperationException("Fitting transitions require the fitting transaction boundary.");
        }
        var magazines = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && Enumerable.Contains(unfittedIds, i.LocationId))
            .OrderBy(i => i.Id)
            .ToArray();
        if (destinationId == ship.Id)
        {
            double capacity = ShipHoldRules.Capacity(flagId, attributes);
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
                .Where(i => !InventoryDefinitions.IsRigSlot(i.FlagId) && (i.LocationId != ship.Id || i.FlagId != flagId))
                .Sum(i => hold.RequiredCapacity(Read(catalog, i), quantity ?? i.Quantity));
            added += magazines.Sum(i => hold.RequiredCapacity(Read(catalog, i), i.Quantity));
            added += storedShips
                .Where(s => s.StoredInId != ship.Id || s.StorageFlagId != flagId)
                .Sum(s => hold.RequiredCapacity(PilotFleetStore.Read(catalog, s), 1));
            if (
                flagId == (int)InventoryFlag.FrigateEscapeBay
                && world.Ships.Count(s => s.StoredInId == ship.Id && s.StorageFlagId == flagId && !Enumerable.Contains(itemIds, s.Id))
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
            if (InventoryDefinitions.IsRigSlot(item.FlagId))
            {
                world.Remove(item);
                changes.Add(new(previous with { Quantity = 0, Online = false }, previous));
                continue;
            }
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
                            VolumeOverride = item.VolumeOverride,
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
            if (stored.StoredInId is not null)
            {
                stored.StructureId = null;
            }
            if (stored.StoredInId is null)
            {
                stored.StructureId = destinationId >= Eden.Gameplay.Structures.StructureRules.FirstStructureId ? destinationId : null;
                if (stored.StructureId is null)
                {
                    stored.StationId = checked((int)destinationId);
                }
            }
            changes.Add(new(PilotFleetStore.Read(catalog, stored), previous));
        }
        world.SaveChanges();
        return changes;
    }

    private static IReadOnlyList<InventoryChange> SplitInPlace(
        EconomyTransaction world,
        CatalogDbContext catalog,
        OwnedItem stack,
        int quantity
    )
    {
        var previous = Read(catalog, stack);
        var split = InventoryStackRules.Split(previous, quantity);
        if (world.Set<OwnedItem>().Any(item => item.LocationId == stack.Id) || world.Ships.Any(ship => ship.StoredInId == stack.Id))
        {
            throw new InvalidDataException("A container with contents cannot be divided.");
        }
        stack.Quantity = split.Remainder;
        var separated = world
            .Add(
                new OwnedItem
                {
                    Id = InventoryPersistence.NextId(world),
                    OwnerId = stack.OwnerId,
                    TypeId = stack.TypeId,
                    LocationId = stack.LocationId,
                    FlagId = stack.FlagId,
                    Quantity = split.Quantity,
                    CrystalDamage = stack.CrystalDamage,
                    VolumeOverride = stack.VolumeOverride,
                }
            )
            .Entity;
        return [new(Read(catalog, stack), previous), new(Read(catalog, separated), null)];
    }

    private IReadOnlyList<InventoryChange>? TryResidentMove(
        Eden.World.Runtime.LiveWorld live,
        long pilotId,
        long destinationId,
        InventoryMoveRequest request
    )
    {
        using var ownership = live.EnterShared("inventory move");
        using (var selection = live.Economy.Begin(new(pilots: [pilotId])))
        {
            if (
                selection
                    .Set<OwnedItem>()
                    .Where(i => Enumerable.Contains(request.ItemIds, i.Id))
                    .Any(i => InventoryDefinitions.IsFittedSlot(i.FlagId))
            )
            {
                return null;
            }
        }
        // Load once per real fit revision before opening the private economic view.
        var attributes = new Eden.World.Fittings.PilotFittingStore(database, clock).ShipAttributes(pilotId);
        using var transaction = live.Economy.Begin(new(pilots: [pilotId]));
        transaction.SetAttributes(pilotId, attributes);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = Move(transaction, catalog, pilotId, destinationId, request, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), attributes);
        transaction.Commit();
        return result;
    }

    internal static double HoldCapacity(EconomyTransaction world, CatalogDbContext catalog, long pilotId, long shipId, int flagId)
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        if (pilot.ActiveShipId != shipId)
        {
            throw new InvalidDataException("Use the active ship's hold.");
        }
        return ShipHoldRules.Capacity(flagId, world.Attributes(pilotId));
    }
}
