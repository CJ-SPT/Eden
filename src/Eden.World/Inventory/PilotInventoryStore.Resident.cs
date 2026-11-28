using System.Globalization;
using Eden.Data.Catalog;
using Eden.Data.World.Contracts;
using Eden.Data.World.Inventory;
using Eden.Data.World.Structures;
using Eden.Gameplay.Ammunition;
using Eden.Gameplay.Combat;
using Eden.Gameplay.Dogma;
using Eden.Gameplay.Inventory;
using Eden.Gameplay.Ships;
using Eden.Infrastructure.Registration;
using Eden.World.Catalog;
using Eden.World.Corporations;
using Eden.World.Fittings;
using Eden.World.Industry;
using Eden.World.Runtime;
using Eden.World.Runtime.Economy;
using Eden.World.Skills;
using Eden.World.Storage;
using Microsoft.EntityFrameworkCore;

namespace Eden.World.Inventory;

internal sealed partial class PilotInventoryStore
{
    public IReadOnlyList<InventoryItem> PreviewDockedShip(long pilotId, long shipId, long dockableId, int systemId)
    {
        using CatalogDbContext catalog = new(database.CatalogPath);
        return WorldActionScope.Inspect<IReadOnlyList<InventoryItem>>(
            database,
            world =>
            {
                var ship = world.Ships.Find(shipId);
                if (
                    ship is not { Position: null, StoredInId: null }
                    || ship.OwnerId != pilotId
                    || ship.DockedLocationId != dockableId
                    || world.Set<SavedContractEscrow>().Find(shipId) is not null
                )
                {
                    throw new InvalidDataException("Choose a ship in your personal Item Hangar at the requested location.");
                }
                int? actualSystem = ship.StructureId is long structure
                    ? world.Set<SavedStructure>().Find(structure)?.SystemId
                    : catalog.FindStation(ship.StationId)?.SolarSystemId;
                if (actualSystem != systemId)
                {
                    throw new InvalidDataException("The ship is not docked in the requested solar system.");
                }
                var contents = List(world, catalog, pilotId, shipId);
                var slots = contents
                    .Where(item => InventoryDefinitions.IsFittedSlot(item.FlagId))
                    .ToDictionary(item => item.Id, item => item.FlagId);
                var modules = slots.Keys.ToArray();
                // The client builds a fitting from rows at ship scope, including charges in module slots.
                var charges = world
                    .Filter<OwnedItem>(item => item.OwnerId == pilotId && Enumerable.Contains(modules, item.LocationId))
                    .AsEnumerable()
                    .Select(item => Read(catalog, item))
                    .Where(item => item.CategoryId == (int)InventoryCategory.Charge)
                    .Select(item => item with { LocationId = shipId, FlagId = slots[item.LocationId] });
                return contents.Concat(charges).OrderBy(item => item.Id).ToArray();
            },
            consistentFileRead: true
        );
    }

    public IReadOnlyDictionary<long, double> CrystalDamage(long pilotId, IReadOnlyCollection<long> itemIds)
    {
        Dictionary<long, double> result = [];
        if (itemIds.Count == 0)
        {
            return result;
        }
        var live = LiveWorld.Find(database.Path) ?? throw new InvalidDataException("Crystal condition requires the resident world.");
        using CatalogDbContext catalog = new(database.CatalogPath);
        return live.Economy.Inspect(
            world =>
            {
                var items = world.Set<OwnedItem>();
                foreach (long id in itemIds)
                {
                    if (
                        items.Find(id) is not { } item
                        || item.OwnerId != pilotId
                        || catalog.FittingType(item.TypeId)?.CategoryId != (int)InventoryCategory.Charge
                    )
                    {
                        continue;
                    }
                    var attributes = catalog.FittingAttributes(item.TypeId);
                    if (attributes.GetValueOrDefault(DogmaAttributeId.CrystalsGetDamaged) > 0)
                    {
                        double hitPoints = attributes.GetValueOrDefault(DogmaAttributeId.StructureHitPoints);
                        result[id] = CrystalRules.DamageRatio(item.CrystalDamage, hitPoints);
                    }
                }
                return result;
            },
            new(pilots: [pilotId])
        );
    }

    internal static IReadOnlyList<InventoryItem> List(WorldAction world, CatalogDbContext catalog, long pilotId, long locationId)
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
        WorldAction world,
        CatalogDbContext catalog,
        long pilotId,
        long destinationId,
        InventoryMoveRequest request,
        long now,
        IReadOnlyDictionary<DogmaAttributeId, double>? attributes = null,
        bool deferFittingChanges = false,
        Ship? fittingShip = null,
        bool mobileRefit = false,
        bool preserveRigs = false
    )
    {
        var (itemIds, sourceId, flagId, quantity) = request;
        if (Eden.World.Structures.AssetSafetyStore.Ancestor(world, sourceId) is not null)
        {
            return Eden.World.Structures.AssetSafetyStore.Recover(world, catalog, pilotId, destinationId, request, now).Changes;
        }
        if (itemIds.Any(id => world.Set<Eden.Data.World.Structures.SavedAssetSafety>().Find(id) is not null))
        {
            throw new InvalidDataException("Recover the contents of an asset safety wrap before moving its container.");
        }
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var active = pilot.ActiveShip;
        var sourceShip = InventoryShip(world, pilot, sourceId);
        var destinationShip = InventoryShip(world, pilot, destinationId);
        var ship = fittingShip ?? sourceShip ?? destinationShip ?? active;
        if (fittingShip is not null)
        {
            PilotFittingStore.RequireFittingShip(pilot, ship);
        }
        world.RequireUncloaked(active.Id);
        bool sameShipQuantityMove = sourceId == active.Id && destinationId == active.Id && quantity.HasValue;
        bool sourceHangar = active.Position is null && sourceId == active.DockedLocationId;
        bool destinationHangar = active.Position is null && destinationId == active.DockedLocationId;
        bool claimDelivery =
            sourceHangar
            && destinationHangar
            && active.StructureId.HasValue
            && flagId == (int)InventoryFlag.Hangar
            && itemIds.Count > 0
            && world.Set<OwnedItem>().Find(itemIds[0]) is { FlagId: (int)InventoryFlag.StructureDeliveries } first
            && first.OwnerId == pilotId
            && first.LocationId == sourceId;
        if (
            (active.Position is not null && !sameShipQuantityMove && !mobileRefit)
            || !(sourceHangar || sourceShip is not null)
            || !(destinationHangar && flagId == (int)InventoryFlag.Hangar || destinationShip is not null && ShipHoldRules.IsHold(flagId))
            || sourceHangar && destinationHangar && !quantity.HasValue && !claimDelivery
            || itemIds.Count == 0
            || itemIds.Distinct().Count() != itemIds.Count
            || quantity is <= 0
            || (quantity.HasValue && itemIds.Count != 1)
        )
        {
            throw new InvalidDataException("Move items between owned ships and the hangar at your current docked location.");
        }

        var holdFlags = ShipHoldRules.Definitions.Select(h => (int)h.Flag).ToArray();
        int sourceHangarFlag = (int)(claimDelivery ? InventoryFlag.StructureDeliveries : InventoryFlag.Hangar);
        var items = world
            .Set<OwnedItem>()
            .Where(i =>
                Enumerable.Contains(itemIds, i.Id)
                && i.OwnerId == pilotId
                && i.LocationId == sourceId
                && (
                    sourceShip != null
                        ? Enumerable.Contains(holdFlags, i.FlagId)
                            || (i.FlagId >= (int)InventoryFlag.LowSlot0 && i.FlagId <= (int)InventoryFlag.HighSlot7)
                            || (i.FlagId >= (int)InventoryFlag.RigSlot0 && i.FlagId <= (int)InventoryFlag.RigSlot2)
                        : i.FlagId == sourceHangarFlag
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
                && (sourceShip is not null ? s.StoredInId == sourceId : s.StoredInId is null && s.DockedLocationId == sourceId)
            )
            .OrderBy(s => s.Id)
            .ToArray();
        if (
            items.Length + storedShips.Length != itemIds.Count
            || items.Any(i => quantity > i.Quantity)
            || storedShips.Any(s => s.Id == destinationId || quantity is > 1 || world.Pilots.Any(p => p.ActiveShipId == s.Id))
            || (active.Position is not null && !mobileRefit && (items.Any(item => item.FlagId != flagId) || storedShips.Length > 0))
        )
        {
            throw new InvalidDataException("The requested items or quantity are unavailable.");
        }
        if (items.Any(i => PilotIndustryStore.IsInstalled(world, i.Id)))
        {
            throw new InvalidDataException("Deliver or cancel the industry job before moving its blueprint.");
        }
        if (sourceId == destinationId && destinationHangar && storedShips.Length > 0)
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
        if (
            sourceId == destinationId
            && items.All(item => item.FlagId == flagId)
            && storedShips.All(stored => stored.StorageFlagId == flagId)
        )
        {
            return [];
        }
        var unfittedIds = items.Where(i => InventoryDefinitions.IsFittedSlot(i.FlagId)).Select(i => i.Id).ToArray();
        var checkpoint = unfittedIds.Length > 0 && !deferFittingChanges ? FittingChanges.Capture(world, catalog, pilot, now, ship) : null;
        var magazines = world
            .Set<OwnedItem>()
            .Where(i => i.OwnerId == pilotId && Enumerable.Contains(unfittedIds, i.LocationId))
            .OrderBy(i => i.Id)
            .ToArray();
        if (destinationShip is not null)
        {
            var destinationAttributes =
                destinationShip.Id == active.Id && fittingShip is null
                    ? attributes ?? world.Attributes(pilotId)
                    : PilotFittingStore.ShipAttributes(world, catalog, pilot, destinationShip);
            double capacity = ShipHoldRules.Capacity(flagId, destinationAttributes);
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
            double used = InventoryPersistence.UsedVolume(world, catalog, pilotId, destinationId, flagId);
            double added = items
                .Where(i =>
                    (preserveRigs || !InventoryDefinitions.IsRigSlot(i.FlagId)) && (i.LocationId != destinationId || i.FlagId != flagId)
                )
                .Sum(i => hold.RequiredCapacity(Read(catalog, i), quantity ?? i.Quantity));
            added += magazines.Sum(i => hold.RequiredCapacity(Read(catalog, i), i.Quantity));
            added += storedShips
                .Where(s => s.StoredInId != destinationId || s.StorageFlagId != flagId)
                .Sum(s => hold.RequiredCapacity(PilotFleetStore.Read(catalog, s), 1));
            if (
                flagId == (int)InventoryFlag.FrigateEscapeBay
                && world.Ships.Count(s => s.StoredInId == destinationId && s.StorageFlagId == flagId && !Enumerable.Contains(itemIds, s.Id))
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
            if (!preserveRigs && InventoryDefinitions.IsRigSlot(item.FlagId))
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
                            BlueprintRuns = item.BlueprintRuns,
                            MaterialEfficiency = item.MaterialEfficiency,
                            TimeEfficiency = item.TimeEfficiency,
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
            stored.StoredInId = destinationShip?.Id;
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
        if (checkpoint is not null)
        {
            changes.AddRange(FittingChanges.Finish(world, catalog, pilot, checkpoint, now, ship));
        }
        return changes;
    }

    internal static Ship? InventoryShip(WorldAction world, Eden.Data.World.Characters.Pilot pilot, long id)
    {
        var ship = world.Ships.Find(id);
        if (ship is null || ship.Position is not null && ship.Id != pilot.ActiveShipId || ship.StoredInId is not null)
        {
            return null;
        }
        var active = pilot.ActiveShip;
        return InventoryMoveRules.CanUseShip(
            new(pilot.Id, active.Id, active.DockedLocationId, active.Position is null),
            new(ship.Id, ship.OwnerId, ship.DockedLocationId, ship.StorageFlagId)
        )
            ? ship
            : null;
    }

    internal static bool AccessibleItems(WorldAction world, Eden.Data.World.Characters.Pilot pilot, long location, int flag)
    {
        var active = pilot.ActiveShip;
        var ship = InventoryShip(world, pilot, location);
        return InventoryMoveRules.CanUseItems(
            new(pilot.Id, active.Id, active.DockedLocationId, active.Position is null),
            location,
            flag,
            ship is null ? null : new(ship.Id, ship.OwnerId, ship.DockedLocationId, ship.StorageFlagId)
        );
    }

    private static IReadOnlyList<InventoryChange> SplitInPlace(WorldAction world, CatalogDbContext catalog, OwnedItem stack, int quantity)
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
                    BlueprintRuns = stack.BlueprintRuns,
                    MaterialEfficiency = stack.MaterialEfficiency,
                    TimeEfficiency = stack.TimeEfficiency,
                }
            )
            .Entity;
        return [new(Read(catalog, stack), previous), new(Read(catalog, separated), null)];
    }

    private IReadOnlyList<InventoryChange> MoveResident(
        Eden.World.Runtime.LiveWorld live,
        long pilotId,
        long destinationId,
        InventoryMoveRequest request
    )
    {
        using var ownership = live.EnterShared("inventory move");
        // Load once per real fit revision before opening the private economic view.
        var attributes = new Eden.World.Fittings.PilotFittingStore(database, clock).ShipAttributes(pilotId);
        using var transaction = live.Economy.Begin(new(pilots: [pilotId]));
        transaction.SetAttributes(pilotId, attributes);
        using CatalogDbContext catalog = new(database.CatalogPath);
        var result = Move(transaction, catalog, pilotId, destinationId, request, clock.GetUtcNow().UtcDateTime.ToFileTimeUtc(), attributes);
        transaction.Commit();
        return result;
    }

    internal static double HoldCapacity(WorldAction world, CatalogDbContext catalog, long pilotId, long shipId, int flagId)
    {
        var pilot = world.Pilots.Find(pilotId) ?? throw new InvalidDataException("Unknown pilot.");
        var ship = InventoryShip(world, pilot, shipId) ?? throw new InvalidDataException("Use an owned ship at your current location.");
        var attributes =
            ship.Id == pilot.ActiveShipId ? world.Attributes(pilotId) : PilotFittingStore.ShipAttributes(world, catalog, pilot, ship);
        return ShipHoldRules.Capacity(flagId, attributes);
    }
}
